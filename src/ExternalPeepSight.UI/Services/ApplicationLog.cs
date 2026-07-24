using System.Diagnostics;
using System.Text.Json;

namespace ExternalPeepSight.UI.Services;

internal static class ApplicationLog
{
    private const long MaximumLogBytes = 2L * 1024L * 1024L;
    private static readonly object SyncRoot = new();

    public static void Write(string eventName, Exception exception)
    {
        try
        {
            lock (SyncRoot)
            {
                string directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ExternalPeepSight",
                    "logs");
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "ui.log");
                RotateIfNeeded(path);
                string record = JsonSerializer.Serialize(new
                {
                    timestampUtc = DateTimeOffset.UtcNow,
                    processId = Environment.ProcessId,
                    component = "ExternalPeepSight.UI",
                    level = "error",
                    @event = eventName,
                    exceptionType = exception.GetType().Name,
                    message = exception.Message,
                });
                File.AppendAllText(path, record + Environment.NewLine);
            }
        }
        catch (Exception loggingException) when (
            loggingException is IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.WriteLine($"Unable to write UI diagnostic log: {loggingException.Message}");
        }
    }

    private static void RotateIfNeeded(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < MaximumLogBytes)
        {
            return;
        }

        string backup = path + ".1";
        File.Move(path, backup, overwrite: true);
    }
}
