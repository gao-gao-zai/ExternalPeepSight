using System.Text;

namespace ExternalPeepSight.Core;

/// <summary>
/// Persists configuration JSON with temporary-file and backup recovery semantics.
/// </summary>
public sealed class AtomicConfigurationStore
{
    /// <summary>
    /// Gets the required default persistence debounce interval.
    /// </summary>
    public static TimeSpan DefaultDebounce { get; } = TimeSpan.FromMilliseconds(300);

    private readonly object gate = new();

    /// <summary>
    /// Initializes a configuration store.
    /// </summary>
    /// <param name="filePath">The primary configuration file path.</param>
    public AtomicConfigurationStore(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("Configuration file path is required.", nameof(filePath));
        }

        FilePath = Path.GetFullPath(filePath);
        BackupFilePath = $"{FilePath}.bak";
    }

    /// <summary>
    /// Gets the primary configuration path.
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    /// Gets the backup configuration path.
    /// </summary>
    public string BackupFilePath { get; }

    /// <summary>
    /// Loads the primary document, or the backup when the primary is unreadable.
    /// </summary>
    /// <returns>The last valid configuration document.</returns>
    /// <exception cref="ConfigurationFormatException">Both files contain invalid JSON.</exception>
    /// <exception cref="FileNotFoundException">Neither file exists.</exception>
    public ConfigurationDocument Load()
    {
        lock (gate)
        {
            ConfigurationFormatException? primaryFailure = null;
            if (File.Exists(FilePath))
            {
                try
                {
                    return ConfigurationJson.Deserialize(File.ReadAllText(FilePath, Encoding.UTF8));
                }
                catch (ConfigurationFormatException exception)
                {
                    primaryFailure = exception;
                }
                catch (ConfigurationValidationException exception)
                {
                    primaryFailure = new ConfigurationFormatException("Primary configuration failed validation.", exception);
                }
            }

            if (File.Exists(BackupFilePath))
            {
                try
                {
                    return ConfigurationJson.Deserialize(File.ReadAllText(BackupFilePath, Encoding.UTF8));
                }
                catch (ConfigurationFormatException exception)
                {
                    throw new ConfigurationFormatException("Primary and backup configurations are invalid.", exception);
                }
                catch (ConfigurationValidationException exception)
                {
                    throw new ConfigurationFormatException("Primary and backup configurations failed validation.", exception);
                }
            }

            if (primaryFailure is not null)
            {
                throw primaryFailure;
            }

            throw new FileNotFoundException("Configuration file was not found.", FilePath);
        }
    }

    /// <summary>
    /// Saves a validated document using an atomic replacement.
    /// </summary>
    /// <param name="document">The document to save.</param>
    public void Save(ConfigurationDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        string json = ConfigurationJson.Serialize(document, indented: true);
        string? directory = Path.GetDirectoryName(FilePath);
        if (directory is null)
        {
            throw new InvalidOperationException("Configuration path has no directory.");
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(FilePath)}.{Guid.NewGuid():N}.tmp");

        lock (gate)
        {
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.WriteThrough))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(FilePath))
                {
                    File.Replace(temporaryPath, FilePath, BackupFilePath, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temporaryPath, FilePath);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
    }

    /// <summary>
    /// Restores the backup as the primary file.
    /// </summary>
    /// <returns><see langword="true"/> when a backup was restored.</returns>
    public bool TryRestoreBackup()
    {
        lock (gate)
        {
            if (!File.Exists(BackupFilePath))
            {
                return false;
            }

            ConfigurationJson.Deserialize(File.ReadAllText(BackupFilePath, Encoding.UTF8));
            string temporaryPath = Path.Combine(
                Path.GetDirectoryName(FilePath)!,
                $".{Path.GetFileName(FilePath)}.restore.{Guid.NewGuid():N}.tmp");
            try
            {
                File.Copy(BackupFilePath, temporaryPath);
                if (File.Exists(FilePath))
                {
                    File.Replace(temporaryPath, FilePath, null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temporaryPath, FilePath);
                }

                return true;
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
    }

    /// <summary>
    /// Waits for the debounce interval before saving a document.
    /// </summary>
    /// <param name="document">The document to save.</param>
    /// <param name="debounce">The debounce interval.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes after the save.</returns>
    public async Task SaveDebouncedAsync(
        ConfigurationDocument document,
        TimeSpan debounce,
        CancellationToken cancellationToken = default)
    {
        if (debounce < TimeSpan.Zero)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(debounce, TimeSpan.Zero);
        }

        await Task.Delay(debounce, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        Save(document);
    }

    /// <summary>
    /// Saves a document after the standard 300 ms persistence debounce interval.
    /// </summary>
    /// <param name="document">The document to save.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes after the save.</returns>
    public Task SaveDebouncedAsync(
        ConfigurationDocument document,
        CancellationToken cancellationToken = default) =>
        SaveDebouncedAsync(document, DefaultDebounce, cancellationToken);
}
