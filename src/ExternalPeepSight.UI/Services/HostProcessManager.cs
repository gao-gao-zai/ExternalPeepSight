using System.Diagnostics;


using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

using Microsoft.Win32.SafeHandles;

namespace ExternalPeepSight.UI.Services;

internal static class HostProcessManager
{
    private const int TokenElevationInformationClass = 20;
    private static readonly object StartLock = new();
    private static readonly Dictionary<string, long> LastStartAttempts = new(StringComparer.Ordinal);
    private static readonly TimeSpan StartThrottle = TimeSpan.FromSeconds(2);

    public static HostEndpoint? FindOrStart(
        string instanceId,
        bool startHostIfMissing,
        bool requireElevatedInputCompatibility)
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

        string assetRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ExternalPeepSight",
            "assets");
        Directory.CreateDirectory(assetRoot);
        string? uiExecutable = Environment.ProcessPath;
        var startInfo = CreateStartInfo(
            executable,
            instanceId,
            assetRoot,
            !string.IsNullOrWhiteSpace(uiExecutable) && File.Exists(uiExecutable)
                ? Path.GetFullPath(uiExecutable)
                : null,
            requireElevatedInputCompatibility);
        Process.Start(startInfo)?.Dispose();
        return null;
    }

    internal static ProcessStartInfo CreateStartInfo(
        string executable,
        string instanceId,
        string assetRoot,
        string? uiExecutable,
        bool requireElevatedInputCompatibility)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(executable),
            UseShellExecute = requireElevatedInputCompatibility,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable)),
        };
        if (requireElevatedInputCompatibility)
        {
            startInfo.Verb = "runas";
        }

        startInfo.ArgumentList.Add($"--instance-id={instanceId}");
        startInfo.ArgumentList.Add($"--assets-root={Path.GetFullPath(assetRoot)}");
        if (!string.IsNullOrWhiteSpace(uiExecutable))
        {
            startInfo.ArgumentList.Add($"--ui-path={Path.GetFullPath(uiExecutable)}");
        }
        return startInfo;
    }

    internal static bool IsCurrentProcessElevated()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAccessLevels.Query, out SafeAccessTokenHandle token))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to open the UI process token.");
        }

        using (token)
        {
            int tokenElevationSize = Marshal.SizeOf<TokenElevation>();
            if (!GetTokenInformation(
                    token,
                    TokenElevationInformationClass,
                    out TokenElevation elevation,
                    tokenElevationSize,
                    out int returnedBytes))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to query the UI process elevation.");
            }
            if (returnedBytes != tokenElevationSize)
            {
                throw new InvalidOperationException("The UI process elevation result has an invalid size.");
            }

            return elevation.TokenIsElevated != 0;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        nint processHandle,
        TokenAccessLevels desiredAccess,
        out SafeAccessTokenHandle tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle tokenHandle,
        int tokenInformationClass,
        out TokenElevation tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct TokenElevation
    {
        public readonly int TokenIsElevated;
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
