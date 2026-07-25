using System.Diagnostics;

using System.ComponentModel;

namespace ExternalPeepSight.UI.Services;

internal sealed record HostEndpoint(string PipeName, string Token, int ProcessId, bool IsElevated)
{
    private const int ProtocolVersion = 1;

    public static HostEndpoint? TryRead(string instanceId)
    {
        string path = GetDiscoveryPath(instanceId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            Dictionary<string, string> values = File.ReadAllLines(path)
                .Select(line => line.Split('=', 2))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);

            bool hasElevationMetadata = values.TryGetValue("elevated", out string? elevatedText);
            if (values.Count != (hasElevationMetadata ? 5 : 4) ||
                !values.TryGetValue("protocolVersion", out string? protocolText) ||
                !int.TryParse(protocolText, out int protocol) ||
                protocol != ProtocolVersion ||
                !values.TryGetValue("pipeName", out string? pipeName) ||
                !pipeName.StartsWith(
                    $@"\\.\pipe\ExternalPeepSight.{instanceId}.",
                    StringComparison.Ordinal) ||
                !values.TryGetValue("token", out string? token) ||
                token.Length != 64 ||
                token.Any(character =>
                    character is not (>= '0' and <= '9') and
                    not (>= 'a' and <= 'f')) ||
                !values.TryGetValue("processId", out string? processText) ||
                !int.TryParse(processText, out int processId) ||
                processId <= 0 ||
                hasElevationMetadata && elevatedText is not "0" and not "1" ||
                !IsProcessRunning(processId))
            {
                return null;
            }

            return new HostEndpoint(
                pipeName,
                token,
                processId,
                hasElevationMetadata && elevatedText == "1");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public static string GetDiscoveryPath(string instanceId)
    {
        ValidateInstanceId(instanceId);
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ExternalPeepSight",
            $"host-{instanceId}.endpoint");
    }

    public string GetPipeNameForClient() => PipeName[@"\\.\pipe\".Length..];

    public static void ValidateInstanceId(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        if (instanceId.Length > 64 ||
            instanceId.Any(character =>
                character is not (>= '0' and <= '9') and
                not (>= 'A' and <= 'Z') and
                not (>= 'a' and <= 'z') and
                not '-' and
                not '_'))
        {
            throw new ArgumentException("Host instance identifier is invalid.", nameof(instanceId));
        }
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }
}
