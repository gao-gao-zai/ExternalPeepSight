using System.Diagnostics;

using System.IO;

namespace ExternalPeepSight.UI.Services;

internal static class HostProcessManager
{
    private static readonly object StartLock = new();
    private static readonly Dictionary<string, long> LastStartAttempts = new(StringComparer.Ordinal);
    private static readonly TimeSpan StartThrottle = TimeSpan.FromSeconds(2);

    public static HostEndpoint? FindOrStart(string instanceId, bool startHostIfMissing)
    {
        HostEndpoint? endpoint = HostEndpoint.TryRead(instanceId);
        if (endpoint is not null)
        {
            lock (StartLock)
            {
                LastStartAttempts.Remove(instanceId);
            }
            return endpoint;
        }

        if (!startHostIfMissing || !ReserveStartAttempt(instanceId))
        {
            return null;
        }

        string? executable = ResolveHostExecutable();
        if (executable is null)
        {
            return null;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable),
        };
        startInfo.ArgumentList.Add($"--instance-id={instanceId}");
        string? uiExecutable = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(uiExecutable) && File.Exists(uiExecutable))
        {
            startInfo.ArgumentList.Add($"--ui-path={Path.GetFullPath(uiExecutable)}");
        }
        Process.Start(startInfo)?.Dispose();
        return null;
    }

    public static bool HasGracefulShutdown(string instanceId, int? expectedProcessId = null)
    {
        string path = GetGracefulShutdownPath(instanceId);
        try
        {
            string[] lines = File.ReadAllLines(path);
            if (lines.Length != 1 ||
                !lines[0].StartsWith("processId=", StringComparison.Ordinal) ||
                !int.TryParse(lines[0]["processId=".Length..], out int processId) ||
                processId <= 0)
            {
                return false;
            }

            return expectedProcessId is null || processId == expectedProcessId;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static void ClearGracefulShutdown(string instanceId)
    {
        File.Delete(GetGracefulShutdownPath(instanceId));
    }

    public static string GetGracefulShutdownPath(string instanceId)
    {
        string endpointPath = HostEndpoint.GetDiscoveryPath(instanceId);
        return Path.ChangeExtension(endpointPath, ".shutdown");
    }

    private static bool ReserveStartAttempt(string instanceId)
    {
        long now = Stopwatch.GetTimestamp();
        lock (StartLock)
        {
            if (LastStartAttempts.TryGetValue(instanceId, out long previous) &&
                Stopwatch.GetElapsedTime(previous, now) < StartThrottle)
            {
                return false;
            }

            LastStartAttempts[instanceId] = now;
            return true;
        }
    }

    private static string? ResolveHostExecutable()
    {
        string? configured = Environment.GetEnvironmentVariable("EXTERNAL_PEEPSIGHT_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        string sibling = Path.Combine(AppContext.BaseDirectory, "ExternalPeepSight.Host.exe");
        return File.Exists(sibling) ? sibling : null;
    }
}
