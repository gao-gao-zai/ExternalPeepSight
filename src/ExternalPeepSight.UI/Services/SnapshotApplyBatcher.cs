using System.Text.Json;

namespace ExternalPeepSight.UI.Services;

internal sealed class SnapshotApplyBatcher : IAsyncDisposable
{
    internal static readonly TimeSpan BatchWindow = TimeSpan.FromMilliseconds(16);

    private readonly Func<ulong, JsonElement, CancellationToken, Task> sender;
    private readonly CancellationTokenSource shutdown = new();
    private readonly object sync = new();
    private PendingSnapshot? pending;
    private Task? worker;
    private ulong highestVersion;

    public SnapshotApplyBatcher(
        Func<ulong, JsonElement, CancellationToken, Task> sender)
    {
        this.sender = sender ?? throw new ArgumentNullException(nameof(sender));
    }

    public Task QueueAsync(
        ulong version,
        JsonElement snapshot,
        CancellationToken cancellationToken)
    {
        var completion =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (sync)
        {
            if (version <= highestVersion)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(version),
                    "Queued snapshot versions must increase strictly.");
            }

            highestVersion = version;
            if (pending is null)
            {
                pending = new PendingSnapshot(version, snapshot.Clone(), [completion]);
            }
            else
            {
                pending.Version = version;
                pending.Snapshot = snapshot.Clone();
                pending.Completions.Add(completion);
            }

            worker ??= RunAsync();
        }

        return completion.Task.WaitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        shutdown.Cancel();
        Task? current;
        lock (sync)
        {
            current = worker;
        }

        if (current is not null)
        {
            try
            {
                await current.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
            }
        }

        shutdown.Dispose();
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(BatchWindow, shutdown.Token).ConfigureAwait(false);
                PendingSnapshot? batch;
                lock (sync)
                {
                    batch = pending;
                    pending = null;
                    if (batch is null)
                    {
                        worker = null;
                        return;
                    }
                }

                try
                {
                    await sender(batch.Version, batch.Snapshot, shutdown.Token)
                        .ConfigureAwait(false);
                    foreach (TaskCompletionSource completion in batch.Completions)
                    {
                        completion.TrySetResult();
                    }
                }
                catch (Exception exception)
                {
                    foreach (TaskCompletionSource completion in batch.Completions)
                    {
                        completion.TrySetException(exception);
                    }
                }

                lock (sync)
                {
                    if (pending is null)
                    {
                        worker = null;
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            PendingSnapshot? abandoned;
            lock (sync)
            {
                abandoned = pending;
                pending = null;
                worker = null;
            }
            if (abandoned is not null)
            {
                foreach (TaskCompletionSource completion in abandoned.Completions)
                {
                    completion.TrySetCanceled(shutdown.Token);
                }
            }
        }
    }

    private sealed class PendingSnapshot(
        ulong version,
        JsonElement snapshot,
        List<TaskCompletionSource> completions)
    {
        public ulong Version { get; set; } = version;

        public JsonElement Snapshot { get; set; } = snapshot;

        public List<TaskCompletionSource> Completions { get; } = completions;
    }
}
