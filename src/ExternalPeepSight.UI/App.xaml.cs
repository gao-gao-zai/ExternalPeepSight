using System.Windows;

using ExternalPeepSight.UI.Services;

namespace ExternalPeepSight.UI;

/// <summary>
/// Owns the settings process connection to the native Host.
/// </summary>
public partial class App : Application, IDisposable
{
    private HostClient? hostClient;

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        string instanceId = GetOptionValue(e.Args, "--instance-id=") ?? "default";
        bool startHostIfMissing = !e.Args.Contains("--connect-existing", StringComparer.Ordinal);
        if (startHostIfMissing)
        {
            HostProcessManager.ClearGracefulShutdown(instanceId);
        }

        hostClient = new HostClient(instanceId, startHostIfMissing);
        hostClient.HostExited += OnHostExited;
        hostClient.Start();

        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        Dispose();
        base.OnExit(e);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (hostClient is not null)
        {
            hostClient.HostExited -= OnHostExited;
            hostClient.DisposeAsync().AsTask().GetAwaiter().GetResult();
            hostClient = null;
        }

        GC.SuppressFinalize(this);
    }

    private void OnHostExited(object? sender, EventArgs e)
    {
        Shutdown();
    }

    private static string? GetOptionValue(IEnumerable<string> arguments, string prefix)
    {
        foreach (string argument in arguments)
        {
            if (argument.StartsWith(prefix, StringComparison.Ordinal))
            {
                string value = argument[prefix.Length..];
                if (value.Length == 0)
                {
                    throw new ArgumentException($"Command-line option {prefix} requires a value.");
                }

                return value;
            }
        }

        return null;
    }
}

