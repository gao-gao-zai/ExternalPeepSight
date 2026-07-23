using System.Text.Json;
using ExternalPeepSight.UI.Services;

namespace ExternalPeepSight.UI.Tests;

public sealed class SnapshotApplyBatcherTests
{
    [Fact]
    public async Task RapidUpdatesSendOnlyNewestSnapshotAfterBatchWindow()
    {
        var sent = new List<(ulong Version, int Value)>();
        await using var batcher = new SnapshotApplyBatcher(
            (version, snapshot, _) =>
            {
                sent.Add((version, snapshot.GetProperty("value").GetInt32()));
                return Task.CompletedTask;
            });
        using JsonDocument first = JsonDocument.Parse("""{"value":1}""");
        using JsonDocument second = JsonDocument.Parse("""{"value":2}""");

        Task firstCompletion = batcher.QueueAsync(1, first.RootElement, CancellationToken.None);
        Task secondCompletion = batcher.QueueAsync(2, second.RootElement, CancellationToken.None);
        await Task.WhenAll(firstCompletion, secondCompletion);

        Assert.Equal([(2UL, 2)], sent);
    }

    [Fact]
    public async Task StaleVersionCannotReplaceNewerQueuedUpdate()
    {
        await using var batcher = new SnapshotApplyBatcher(
            (_, _, _) => Task.CompletedTask);
        using JsonDocument document = JsonDocument.Parse("{}");

        Task accepted = batcher.QueueAsync(5, document.RootElement, CancellationToken.None);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => batcher.QueueAsync(4, document.RootElement, CancellationToken.None));
        await accepted;
    }
}
