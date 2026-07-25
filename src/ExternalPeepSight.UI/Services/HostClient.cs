using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO.Pipes;
using System.Text.Json;

namespace ExternalPeepSight.UI.Services;

/// <summary>
/// Maintains the authenticated settings connection to the native Host.
/// </summary>
internal interface IHostSession
{
    public event EventHandler<JsonElement>? StateChanged;

    public event EventHandler<bool>? ConnectionChanged;

    public bool IsConnected { get; }

    public void Start();

    public Task QueueSnapshotAsync(
        ulong configurationVersion,
        JsonElement snapshot,
        CancellationToken cancellationToken = default);
}

internal interface IScriptValidationSession
{
    public Task<JsonElement> ValidateScriptAsync(
        JsonElement payload,
        CancellationToken cancellationToken = default);
}

internal enum HostLaunchFailure
{
    ElevationCancelled,
}

internal interface IHostLaunchModeSession
{
    public event EventHandler<HostLaunchFailure>? HostLaunchFailed;

    public Task SetElevatedInputCompatibilityAsync(
        bool enabled,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Maintains the authenticated settings connection to the native Host.
/// </summary>
public sealed class HostClient : IAsyncDisposable, IHostSession, IScriptValidationSession, IHostLaunchModeSession
{
    private const int ProtocolVersion = 1;
    private const int MaximumMessageBytes = 1024 * 1024;
    private const int ErrorCancelled = 1223;
    private const ulong MaximumJsonInteger = 9_007_199_254_740_991;
    private readonly string instanceId;
    private readonly bool startHostIfMissing;
    private readonly bool uiProcessElevated;
    private readonly CancellationTokenSource shutdown = new();
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly object streamLock = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<JsonElement>> pending = new();
    private readonly SnapshotApplyBatcher snapshotBatcher;
    private NamedPipeClientStream? stream;
    private Task? connectionLoop;
    private SynchronizationContext? eventContext;
    private bool requireElevatedInputCompatibility;
    private bool? connectedHostElevated;
    private bool isConnected;

    /// <summary>
    /// Creates a client for one Host instance namespace.
    /// </summary>
    public HostClient(
        string instanceId = "default",
        bool startHostIfMissing = true,
        bool requireElevatedInputCompatibility = false)
        : this(
            instanceId,
            startHostIfMissing,
            requireElevatedInputCompatibility,
            HostProcessManager.IsCurrentProcessElevated())
    {
    }

    internal HostClient(
        string instanceId,
        bool startHostIfMissing,
        bool requireElevatedInputCompatibility,
        bool uiProcessElevated)
    {
        HostEndpoint.ValidateInstanceId(instanceId);
        this.instanceId = instanceId;
        this.startHostIfMissing = startHostIfMissing;
        this.requireElevatedInputCompatibility = requireElevatedInputCompatibility;
        this.uiProcessElevated = uiProcessElevated;
        snapshotBatcher = new SnapshotApplyBatcher(ApplySnapshotAsync);
    }

    /// <summary>
    /// Raised after reconnect state synchronization or a Host state notification.
    /// </summary>
    public event EventHandler<JsonElement>? StateChanged;

    /// <summary>
    /// Raised when the authenticated Host connection state changes.
    /// </summary>
    public event EventHandler<bool>? ConnectionChanged;

    /// <summary>
    /// Raised when the connected Host completes a user-requested graceful exit.
    /// </summary>
    public event EventHandler? HostExited;

    event EventHandler<HostLaunchFailure>? IHostLaunchModeSession.HostLaunchFailed
    {
        add => hostLaunchFailed += value;
        remove => hostLaunchFailed -= value;
    }

    private event EventHandler<HostLaunchFailure>? hostLaunchFailed;

    /// <summary>
    /// Gets whether an authenticated Host connection is active.
    /// </summary>
    public bool IsConnected => isConnected;

    /// <summary>
    /// Starts the background connection and reconnection loop.
    /// </summary>
    public void Start()
    {
        if (connectionLoop is not null)
        {
            throw new InvalidOperationException("Host client has already been started.");
        }

        eventContext = SynchronizationContext.Current;
        connectionLoop = RunConnectionLoopAsync(shutdown.Token);
    }

    /// <summary>
    /// Sends a versioned immutable configuration snapshot.
    /// </summary>
    public async Task ApplySnapshotAsync(
        ulong configurationVersion,
        JsonElement snapshot,
        CancellationToken cancellationToken = default)
    {
        if (configurationVersion == 0 || configurationVersion > MaximumJsonInteger)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configurationVersion),
                $"Configuration version must be between 1 and {MaximumJsonInteger}.");
        }

        await SendRequestAsync(
            "ApplySnapshot",
            new { configurationVersion, snapshot },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Queues a snapshot and sends only the newest update in each 16 ms hot-reload window.
    /// </summary>
    public Task QueueSnapshotAsync(
        ulong configurationVersion,
        JsonElement snapshot,
        CancellationToken cancellationToken = default)
    {
        if (configurationVersion == 0 || configurationVersion > MaximumJsonInteger)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configurationVersion),
                $"Configuration version must be between 1 and {MaximumJsonInteger}.");
        }

        return snapshotBatcher.QueueAsync(configurationVersion, snapshot, cancellationToken);
    }

    /// <summary>
    /// Shows a prioritized Host-rendered status Toast.
    /// </summary>
    public async Task ShowToastAsync(
        string id,
        string deduplicationKey,
        string text,
        string category,
        int priority = 0,
        int? durationMs = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(deduplicationKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        if (priority is < -100 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(priority));
        }
        if (durationMs is < 100 or > 60_000)
        {
            throw new ArgumentOutOfRangeException(nameof(durationMs));
        }

        await SendRequestAsync(
            "ShowToast",
            new { id, deduplicationKey, text, category, priority, durationMs },
            cancellationToken).ConfigureAwait(false);
    }

    public Task<JsonElement> ValidateScriptAsync(
        JsonElement payload,
        CancellationToken cancellationToken = default) =>
        SendRequestAsync("ValidateScript", payload, cancellationToken);

    async Task IHostLaunchModeSession.SetElevatedInputCompatibilityAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        Volatile.Write(ref requireElevatedInputCompatibility, enabled);
        bool requireElevation = ResolveHostElevationRequirement(enabled, uiProcessElevated);
        bool restartRequired;
        lock (streamLock)
        {
            restartRequired = stream is not null &&
                              connectedHostElevated is bool elevated &&
                              elevated != requireElevation;
        }
        if (restartRequired)
        {
            await SendRequestAsync("RestartHost", payload: null, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await snapshotBatcher.DisposeAsync().ConfigureAwait(false);
        shutdown.Cancel();
        NamedPipeClientStream? current;
        lock (streamLock)
        {
            current = stream;
            stream = null;
        }
        current?.Dispose();

        if (connectionLoop is not null)
        {
            try
            {
                await connectionLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
            }
        }

        FailPending(new OperationCanceledException("Host client stopped."));
        writeLock.Dispose();
        shutdown.Dispose();
    }

    private async Task RunConnectionLoopAsync(CancellationToken cancellationToken)
    {
        TimeSpan retryDelay = TimeSpan.FromMilliseconds(100);
        while (!cancellationToken.IsCancellationRequested)
        {
            if (HostProcessManager.HasGracefulShutdown(instanceId))
            {
                RaiseHostExited();
                return;
            }

            NamedPipeClientStream? connectedStream = null;
            Task? receiveLoop = null;
            bool waitForLaunchModeChange = false;
            bool cancelledElevationMode = false;
            try
            {
                bool requireElevation = ResolveHostElevationRequirement(
                    Volatile.Read(ref requireElevatedInputCompatibility),
                    uiProcessElevated);
                HostEndpoint? endpoint =
                    HostProcessManager.FindOrStart(instanceId, startHostIfMissing, requireElevation);
                if (endpoint is null)
                {
                    await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                    retryDelay = IncreaseDelay(retryDelay);
                    continue;
                }

                connectedStream = new NamedPipeClientStream(
                    ".",
                    endpoint.GetPipeNameForClient(),
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);
                await connectedStream.ConnectAsync(2000, cancellationToken).ConfigureAwait(false);
                connectedStream.ReadMode = PipeTransmissionMode.Message;
                lock (streamLock)
                {
                    stream = connectedStream;
                    connectedHostElevated = endpoint.IsElevated;
                }

                receiveLoop = ReceiveLoopAndFailPendingAsync(
                    connectedStream,
                    cancellationToken);
                await SendRequestAsync("Hello", new { token = endpoint.Token }, cancellationToken)
                    .ConfigureAwait(false);
                requireElevation = ResolveHostElevationRequirement(
                    Volatile.Read(ref requireElevatedInputCompatibility),
                    uiProcessElevated);
                if (endpoint.IsElevated != requireElevation)
                {
                    await SendRequestAsync("RestartHost", payload: null, cancellationToken)
                        .ConfigureAwait(false);
                    await receiveLoop.ConfigureAwait(false);
                }
                else
                {
                    SetConnected(true);
                    JsonElement state = await SendRequestAsync("GetState", payload: null, cancellationToken)
                        .ConfigureAwait(false);
                    RaiseStateChanged(state);
                    retryDelay = TimeSpan.FromMilliseconds(100);
                    await receiveLoop.ConfigureAwait(false);
                }
            }
            catch (Win32Exception exception) when (
                exception.NativeErrorCode == ErrorCancelled &&
                Volatile.Read(ref requireElevatedInputCompatibility))
            {
                FailPending(exception);
                cancelledElevationMode = true;
                waitForLaunchModeChange = true;
                RaiseHostLaunchFailed(HostLaunchFailure.ElevationCancelled);
            }
            catch (Exception exception) when (IsRecoverableConnectionException(exception))
            {
                FailPending(exception);
                if (HostProcessManager.HasGracefulShutdown(instanceId))
                {
                    RaiseHostExited();
                    return;
                }
            }
            finally
            {
                SetConnected(false);
                lock (streamLock)
                {
                    if (ReferenceEquals(stream, connectedStream))
                    {
                        stream = null;
                        connectedHostElevated = null;
                    }
                }
                connectedStream?.Dispose();
                if (receiveLoop is not null)
                {
                    try
                    {
                        await receiveLoop.ConfigureAwait(false);
                    }
                    catch (Exception exception) when (
                        IsRecoverableConnectionException(exception) ||
                        exception is OperationCanceledException)
                    {
                    }
                }
            }

            if (waitForLaunchModeChange)
            {
                await WaitForLaunchModeChangeAsync(cancelledElevationMode, cancellationToken).ConfigureAwait(false);
                retryDelay = TimeSpan.FromMilliseconds(100);
            }
            else
            {
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                retryDelay = IncreaseDelay(retryDelay);
            }
        }
    }

    private async Task<JsonElement> SendRequestAsync(
        string type,
        object? payload,
        CancellationToken cancellationToken)
    {
        NamedPipeClientStream current;
        lock (streamLock)
        {
            current = stream ?? throw new IOException("Host is not connected.");
        }

        Guid requestId = Guid.NewGuid();
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(requestId, completion))
        {
            throw new InvalidOperationException("Unable to register the IPC request.");
        }

        byte[] message = JsonSerializer.SerializeToUtf8Bytes(new
        {
            protocolVersion = ProtocolVersion,
            requestId,
            type,
            payload,
        });
        if (message.Length > MaximumMessageBytes)
        {
            pending.TryRemove(requestId, out _);
            throw new InvalidOperationException("IPC message exceeds the maximum size.");
        }

        byte[] frame = new byte[sizeof(int) + message.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, message.Length);
        message.CopyTo(frame.AsSpan(sizeof(int)));

        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await current.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await current.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            pending.TryRemove(requestId, out _);
            throw;
        }
        finally
        {
            writeLock.Release();
        }

        using CancellationTokenRegistration registration = cancellationToken.Register(
            () =>
            {
                if (pending.TryRemove(requestId, out TaskCompletionSource<JsonElement>? removed))
                {
                    removed.TrySetCanceled(cancellationToken);
                }
            });
        return await completion.Task.ConfigureAwait(false);
    }

    private async Task ReceiveLoopAndFailPendingAsync(
        NamedPipeClientStream connectedStream,
        CancellationToken cancellationToken)
    {
        try
        {
            await ReceiveLoopAsync(connectedStream, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            IsRecoverableConnectionException(exception) ||
            exception is OperationCanceledException)
        {
            FailPending(exception);
            throw;
        }
    }

    private async Task ReceiveLoopAsync(
        NamedPipeClientStream connectedStream,
        CancellationToken cancellationToken)
    {
        byte[] prefix = new byte[sizeof(int)];
        while (!cancellationToken.IsCancellationRequested)
        {
            await connectedStream.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
            int messageLength = BinaryPrimitives.ReadInt32LittleEndian(prefix);
            if (messageLength <= 0 || messageLength > MaximumMessageBytes)
            {
                throw new HostProtocolException("Host response length is invalid.");
            }

            byte[] message = new byte[messageLength];
            await connectedStream.ReadExactlyAsync(message, cancellationToken).ConfigureAwait(false);
            if (!connectedStream.IsMessageComplete)
            {
                throw new HostProtocolException("Host response frame contains trailing data.");
            }

            using JsonDocument document = JsonDocument.Parse(
                message,
                new JsonDocumentOptions { MaxDepth = 64, AllowTrailingCommas = false });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new HostProtocolException("Host response envelope is invalid.");
            }

            ValidateEnvelopeProperties(root);
            if (!root.TryGetProperty("protocolVersion", out JsonElement versionElement) ||
                !versionElement.TryGetInt32(out int version) ||
                version != ProtocolVersion ||
                !root.TryGetProperty("requestId", out JsonElement requestIdElement) ||
                requestIdElement.ValueKind != JsonValueKind.String ||
                !requestIdElement.TryGetGuid(out Guid requestId) ||
                !root.TryGetProperty("type", out JsonElement typeElement) ||
                typeElement.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("payload", out JsonElement payloadElement) ||
                payloadElement.ValueKind is not JsonValueKind.Object and not JsonValueKind.Null)
            {
                throw new HostProtocolException("Host response envelope is invalid.");
            }

            string responseType = typeElement.GetString()
                ?? throw new HostProtocolException("Host response type is missing.");
            if (responseType is not "Ack" and not "Error" and not "HostStateChanged")
            {
                throw new HostProtocolException("Host response type is invalid.");
            }

            JsonElement responsePayload = payloadElement.Clone();
            if (responseType == "HostStateChanged")
            {
                RaiseStateChanged(responsePayload);
            }
            else if (pending.TryRemove(requestId, out TaskCompletionSource<JsonElement>? completion))
            {
                if (responseType == "Ack")
                {
                    completion.TrySetResult(responsePayload);
                }
                else if (responseType == "Error")
                {
                    if (responsePayload.ValueKind != JsonValueKind.Object ||
                        !responsePayload.TryGetProperty("code", out JsonElement codeElement) ||
                        codeElement.ValueKind != JsonValueKind.String ||
                        !responsePayload.TryGetProperty("message", out JsonElement messageElement) ||
                        messageElement.ValueKind != JsonValueKind.String)
                    {
                        completion.TrySetException(
                            new HostProtocolException("Host error payload is invalid."));
                        continue;
                    }

                    string code = codeElement.GetString() ?? "Unknown";
                    string messageText = messageElement.GetString() ?? "Host rejected the request.";
                    completion.TrySetException(new HostRequestException(code, messageText));
                }
            }
        }
    }

    private static void ValidateEnvelopeProperties(JsonElement root)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (property.Name is not "protocolVersion" and not "requestId" and not "type" and not "payload" ||
                !names.Add(property.Name))
            {
                throw new HostProtocolException("Host response envelope contains an invalid property.");
            }
        }

        if (names.Count != 4)
        {
            throw new HostProtocolException("Host response envelope is incomplete.");
        }
    }

    private void RaiseStateChanged(JsonElement state)
    {
        EventHandler<JsonElement>? handler = StateChanged;
        if (handler is null)
        {
            return;
        }

        SynchronizationContext? context = eventContext;
        if (context is null || ReferenceEquals(context, SynchronizationContext.Current))
        {
            handler(this, state);
            return;
        }

        context.Post(
            static value =>
            {
                var invocation =
                    ((HostClient Client, EventHandler<JsonElement> Handler, JsonElement State))value!;
                invocation.Handler(invocation.Client, invocation.State);
            },
            (this, handler, state));
    }

    private void RaiseHostExited()
    {
        EventHandler? handler = HostExited;
        if (handler is null)
        {
            return;
        }

        SynchronizationContext? context = eventContext;
        if (context is null || ReferenceEquals(context, SynchronizationContext.Current))
        {
            handler(this, EventArgs.Empty);
            return;
        }

        context.Post(
            static value =>
            {
                var invocation = ((HostClient Client, EventHandler Handler))value!;
                invocation.Handler(invocation.Client, EventArgs.Empty);
            },
            (this, handler));
    }

    private void RaiseHostLaunchFailed(HostLaunchFailure failure)
    {
        EventHandler<HostLaunchFailure>? handler = hostLaunchFailed;
        if (handler is null)
        {
            return;
        }

        SynchronizationContext? context = eventContext;
        if (context is null || ReferenceEquals(context, SynchronizationContext.Current))
        {
            handler(this, failure);
            return;
        }

        context.Post(
            static value =>
            {
                var invocation =
                    ((HostClient Client, EventHandler<HostLaunchFailure> Handler, HostLaunchFailure Failure))value!;
                invocation.Handler(invocation.Client, invocation.Failure);
            },
            (this, handler, failure));
    }

    private void SetConnected(bool connected)
    {
        if (isConnected == connected)
        {
            return;
        }

        isConnected = connected;
        EventHandler<bool>? handler = ConnectionChanged;
        if (handler is null)
        {
            return;
        }

        SynchronizationContext? context = eventContext;
        if (context is null || ReferenceEquals(context, SynchronizationContext.Current))
        {
            handler(this, connected);
            return;
        }

        context.Post(
            static value =>
            {
                var invocation = ((HostClient Client, EventHandler<bool> Handler, bool Connected))value!;
                invocation.Handler(invocation.Client, invocation.Connected);
            },
            (this, handler, connected));
    }

    private void FailPending(Exception exception)
    {
        foreach ((Guid requestId, TaskCompletionSource<JsonElement> completion) in pending)
        {
            if (pending.TryRemove(requestId, out _))
            {
                if (exception is OperationCanceledException canceled)
                {
                    completion.TrySetCanceled(canceled.CancellationToken);
                }
                else
                {
                    completion.TrySetException(exception);
                }
            }
        }
    }

    private static TimeSpan IncreaseDelay(TimeSpan current) =>
        TimeSpan.FromMilliseconds(Math.Min(current.TotalMilliseconds * 2, 5000));

    internal static bool ResolveHostElevationRequirement(
        bool elevatedInputCompatibility,
        bool uiProcessElevated) =>
        elevatedInputCompatibility || uiProcessElevated;

    private async Task WaitForLaunchModeChangeAsync(
        bool cancelledElevationMode,
        CancellationToken cancellationToken)
    {
        while (Volatile.Read(ref requireElevatedInputCompatibility) == cancelledElevationMode)
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsRecoverableConnectionException(Exception exception) =>
        exception is IOException or TimeoutException or JsonException or HostProtocolException or Win32Exception;

    private sealed class HostProtocolException(string message) : Exception(message);
}

internal sealed class HostRequestException(string code, string message) : Exception($"{code}: {message}")
{
    public string Code { get; } = code;

    public string HostMessage { get; } = message;
}
