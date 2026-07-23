using System.Text;

namespace ExternalPeepSight.Core.Tests;

public sealed class AtomicConfigurationStoreTests
{
    [Fact]
    public void ConstructorRejectsEmptyPath()
    {
        Assert.Throws<ArgumentException>(() => new AtomicConfigurationStore(" "));
    }

    [Fact]
    public void MissingPrimaryAndBackupThrows()
    {
        string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}", "config.json");
        var store = new AtomicConfigurationStore(path);

        Assert.Throws<FileNotFoundException>(store.Load);
        Assert.False(store.TryRestoreBackup());
    }

    [Fact]
    public void InvalidPrimaryWithoutBackupIsReported()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "config.json");
            File.WriteAllText(path, "{", Encoding.UTF8);
            var store = new AtomicConfigurationStore(path);

            Assert.Throws<ConfigurationFormatException>(store.Load);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void InvalidPrimaryAndBackupAreReported()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "config.json");
            File.WriteAllText(path, "{", Encoding.UTF8);
            File.WriteAllText($"{path}.bak", "{", Encoding.UTF8);
            var store = new AtomicConfigurationStore(path);

            ConfigurationFormatException exception =
                Assert.Throws<ConfigurationFormatException>(store.Load);

            Assert.Contains("Primary and backup", exception.Message);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void BackupCanBeRestoredWhenPrimaryIsMissing()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "config.json");
            var store = new AtomicConfigurationStore(path);
            File.WriteAllText(
                store.BackupFilePath,
                ConfigurationJson.Serialize(ConfigurationDefaults.Create()),
                Encoding.UTF8);

            Assert.True(store.TryRestoreBackup());
            Assert.True(File.Exists(store.FilePath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DebouncedSaveWritesAfterDelayAndRejectsNegativeDelay()
    {
        string directory = CreateTempDirectory();
        try
        {
            var store = new AtomicConfigurationStore(Path.Combine(directory, "config.json"));

            await store.SaveDebouncedAsync(ConfigurationDefaults.Create(), TimeSpan.Zero);

            Assert.True(File.Exists(store.FilePath));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                store.SaveDebouncedAsync(ConfigurationDefaults.Create(), TimeSpan.FromMilliseconds(-1)));
            Assert.Equal(TimeSpan.FromMilliseconds(300), AtomicConfigurationStore.DefaultDebounce);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"eps-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
