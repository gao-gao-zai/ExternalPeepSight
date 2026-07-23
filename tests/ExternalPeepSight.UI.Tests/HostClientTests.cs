using ExternalPeepSight.UI.Services;
using System.Text.Json;
using System.Buffers.Binary;
using System.IO.Pipes;

namespace ExternalPeepSight.UI.Tests;

public sealed class HostClientTests
{
    [Fact]
    public void ValidEndpointForCurrentProcessIsAccepted()
    {
        string instanceId = CreateInstanceId();
        string path = HostEndpoint.GetDiscoveryPath(instanceId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            File.WriteAllLines(
                path,
                [
                    "protocolVersion=1",
                    $@"pipeName=\\.\pipe\ExternalPeepSight.{instanceId}.test",
                    $"token={new string('a', 64)}",
                    $"processId={Environment.ProcessId}",
                ]);

            HostEndpoint? endpoint = HostEndpoint.TryRead(instanceId);

            Assert.NotNull(endpoint);
            Assert.Equal($@"ExternalPeepSight.{instanceId}.test", endpoint.GetPipeNameForClient());
            Assert.Equal(Environment.ProcessId, endpoint.ProcessId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void EndpointForAnotherInstanceNamespaceIsRejected()
    {
        string instanceId = CreateInstanceId();
        string path = HostEndpoint.GetDiscoveryPath(instanceId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            File.WriteAllLines(
                path,
                [
                    "protocolVersion=1",
                    @"pipeName=\\.\pipe\ExternalPeepSight.another.test",
                    $"token={new string('b', 64)}",
                    $"processId={Environment.ProcessId}",
                ]);

            Assert.Null(HostEndpoint.TryRead(instanceId));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void EndpointWithDuplicateFieldIsRejected()
    {
        string instanceId = CreateInstanceId();
        string path = HostEndpoint.GetDiscoveryPath(instanceId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            File.WriteAllLines(
                path,
                [
                    "protocolVersion=1",
                    $@"pipeName=\\.\pipe\ExternalPeepSight.{instanceId}.test",
                    $"token={new string('c', 64)}",
                    $"token={new string('d', 64)}",
                    $"processId={Environment.ProcessId}",
                ]);

            Assert.Null(HostEndpoint.TryRead(instanceId));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GracefulShutdownMarkerMustMatchExpectedProcess()
    {
        string instanceId = CreateInstanceId();
        string path = HostProcessManager.GetGracefulShutdownPath(instanceId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            File.WriteAllLines(path, [$"processId={Environment.ProcessId}"]);

            Assert.True(
                HostProcessManager.HasGracefulShutdown(instanceId, Environment.ProcessId));
            Assert.False(
                HostProcessManager.HasGracefulShutdown(instanceId, Environment.ProcessId + 1));

            HostProcessManager.ClearGracefulShutdown(instanceId);
            Assert.False(HostProcessManager.HasGracefulShutdown(instanceId));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("contains space")]
    [InlineData("contains.dot")]
    [InlineData("包含中文")]
    public void InvalidInstanceIdentifierIsRejected(string instanceId)
    {
        Assert.ThrowsAny<ArgumentException>(() => HostEndpoint.ValidateInstanceId(instanceId));
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(9_007_199_254_740_992UL)]
    public async Task ConfigurationVersionOutsideJsonIntegerRangeIsRejected(ulong version)
    {
        await using var client = new HostClient(CreateInstanceId(), startHostIfMissing: false);
        using JsonDocument document = JsonDocument.Parse("{}");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => client.ApplySnapshotAsync(version, document.RootElement));
    }

    [Fact]
    public async Task StartingClientTwiceIsRejected()
    {
        await using var client = new HostClient(CreateInstanceId(), startHostIfMissing: false);

        client.Start();

        Assert.Throws<InvalidOperationException>(client.Start);
    }

    [Fact]
    public async Task GracefulShutdownMarkerRaisesEventInsteadOfRestarting()
    {
        string instanceId = CreateInstanceId();
        string pipeName = $"ExternalPeepSight.{instanceId}.graceful-exit";
        string token = new('f', 64);
        string endpointPath = HostEndpoint.GetDiscoveryPath(instanceId);
        Directory.CreateDirectory(Path.GetDirectoryName(endpointPath)!);
        File.WriteAllLines(
            endpointPath,
            [
                "protocolVersion=1",
                $@"pipeName=\\.\pipe\{pipeName}",
                $"token={token}",
                $"processId={Environment.ProcessId}",
            ]);

        using var serverShutdown = new CancellationTokenSource();
        Task serverTask = RunSingleConnectionServerAsync(
            pipeName,
            token,
            HostProcessManager.GetGracefulShutdownPath(instanceId),
            Environment.ProcessId,
            serverShutdown.Token);
        var hostExited =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new HostClient(instanceId, startHostIfMissing: true);
        client.HostExited += (_, _) => hostExited.TrySetResult();

        try
        {
            await Task.Run(client.Start);
            await hostExited.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            serverShutdown.Cancel();
            await client.DisposeAsync();
            File.Delete(endpointPath);
            File.Delete(HostProcessManager.GetGracefulShutdownPath(instanceId));
        }
    }

    [Fact]
    public async Task ClientReconnectsSynchronizesStateAndDisconnectsWithoutStoppingServer()
    {
        string instanceId = CreateInstanceId();
        string pipeName = $"ExternalPeepSight.{instanceId}.integration";
        string token = new('e', 64);
        string endpointPath = HostEndpoint.GetDiscoveryPath(instanceId);
        Directory.CreateDirectory(Path.GetDirectoryName(endpointPath)!);
        File.WriteAllLines(
            endpointPath,
            [
                "protocolVersion=1",
                $@"pipeName=\\.\pipe\{pipeName}",
                $"token={token}",
                $"processId={Environment.ProcessId}",
            ]);

        using var serverShutdown = new CancellationTokenSource();
        var serverObservedDisconnect =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task serverTask = RunReconnectServerAsync(
            pipeName,
            token,
            serverObservedDisconnect,
            serverShutdown.Token);
        var synchronizedVersion =
            new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new HostClient(instanceId, startHostIfMissing: false);
        client.StateChanged += (_, state) =>
        {
            if (state.TryGetProperty("configurationVersion", out JsonElement version) &&
                version.TryGetUInt64(out ulong value) &&
                value == 2)
            {
                synchronizedVersion.TrySetResult(value);
            }
        };

        try
        {
            await Task.Run(client.Start);
            Assert.Equal(
                2UL,
                await synchronizedVersion.Task.WaitAsync(TimeSpan.FromSeconds(5)));

            using JsonDocument snapshot = JsonDocument.Parse("""{"profile":"integration"}""");
            await client.ApplySnapshotAsync(3, snapshot.RootElement)
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await client.DisposeAsync();
        }

        try
        {
            await serverObservedDisconnect.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            serverShutdown.Cancel();
            File.Delete(endpointPath);
        }
    }

    private static async Task RunReconnectServerAsync(
        string pipeName,
        string token,
        TaskCompletionSource serverObservedDisconnect,
        CancellationToken cancellationToken)
    {
        for (int connection = 1; connection <= 2; connection++)
        {
            await using var server = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Message,
                PipeOptions.Asynchronous);
            await server.WaitForConnectionAsync(cancellationToken);

            IpcRequest hello = await ReadRequestAsync(server, cancellationToken);
            Assert.Equal("Hello", hello.Type);
            Assert.Equal(token, hello.Payload.GetProperty("token").GetString());
            await WriteResponseAsync(
                server,
                hello.RequestId,
                new
                {
                    command = "Hello",
                    hostProcessId = Environment.ProcessId,
                    protocolVersion = 1,
                },
                cancellationToken);

            IpcRequest getState = await ReadRequestAsync(server, cancellationToken);
            Assert.Equal("GetState", getState.Type);
            await WriteResponseAsync(
                server,
                getState.RequestId,
                new
                {
                    command = "GetState",
                    configurationVersion = connection,
                    snapshot = new { connection },
                },
                cancellationToken);

            if (connection == 1)
            {
                continue;
            }

            try
            {
                IpcRequest applySnapshot = await ReadRequestAsync(server, cancellationToken);
                Assert.Equal("ApplySnapshot", applySnapshot.Type);
                Assert.Equal(
                    3UL,
                    applySnapshot.Payload.GetProperty("configurationVersion").GetUInt64());
                await WriteResponseAsync(
                    server,
                    applySnapshot.RequestId,
                    new
                    {
                        command = "ApplySnapshot",
                        configurationVersion = 3,
                        alreadyApplied = false,
                    },
                    cancellationToken);

                await ReadRequestAsync(server, cancellationToken);
                Assert.Fail("Client sent an unexpected command while shutting down.");
            }
            catch (IOException)
            {
                serverObservedDisconnect.TrySetResult();
            }
        }
    }

    private static async Task RunSingleConnectionServerAsync(
        string pipeName,
        string token,
        string gracefulShutdownPath,
        int hostProcessId,
        CancellationToken cancellationToken)
    {
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Message,
            PipeOptions.Asynchronous);
        await server.WaitForConnectionAsync(cancellationToken);

        IpcRequest hello = await ReadRequestAsync(server, cancellationToken);
        Assert.Equal("Hello", hello.Type);
        Assert.Equal(token, hello.Payload.GetProperty("token").GetString());
        await WriteResponseAsync(
            server,
            hello.RequestId,
            new
            {
                command = "Hello",
                hostProcessId = Environment.ProcessId,
                protocolVersion = 1,
            },
            cancellationToken);

        IpcRequest getState = await ReadRequestAsync(server, cancellationToken);
        Assert.Equal("GetState", getState.Type);
        await WriteResponseAsync(
            server,
            getState.RequestId,
            new
            {
                command = "GetState",
                configurationVersion = 0,
                snapshot = (object?)null,
            },
            cancellationToken);

        File.WriteAllLines(gracefulShutdownPath, [$"processId={hostProcessId}"]);
    }

    private static async Task<IpcRequest> ReadRequestAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        byte[] prefix = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(prefix, cancellationToken);
        int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        Assert.InRange(length, 1, 1024 * 1024);

        byte[] message = new byte[length];
        await stream.ReadExactlyAsync(message, cancellationToken);
        using JsonDocument document = JsonDocument.Parse(message);
        JsonElement root = document.RootElement;
        return new IpcRequest(
            root.GetProperty("requestId").GetGuid(),
            root.GetProperty("type").GetString()!,
            root.GetProperty("payload").Clone());
    }

    private static async Task WriteResponseAsync(
        Stream stream,
        Guid requestId,
        object payload,
        CancellationToken cancellationToken)
    {
        byte[] message = JsonSerializer.SerializeToUtf8Bytes(new
        {
            protocolVersion = 1,
            requestId,
            type = "Ack",
            payload,
        });
        byte[] frame = new byte[sizeof(int) + message.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, message.Length);
        message.CopyTo(frame.AsSpan(sizeof(int)));
        await stream.WriteAsync(frame, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static string CreateInstanceId() =>
        $"test-{Environment.ProcessId}-{Guid.NewGuid():N}";

    private sealed record IpcRequest(Guid RequestId, string Type, JsonElement Payload);
}
