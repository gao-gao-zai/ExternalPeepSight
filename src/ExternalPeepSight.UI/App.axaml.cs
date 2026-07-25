using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ExternalPeepSight.Core;
using ExternalPeepSight.UI.Services;
using ExternalPeepSight.UI.ViewModels;

namespace ExternalPeepSight.UI;

/// <summary>
/// Owns the settings process connection to the native Host.
/// </summary>
public partial class App : Application, IDisposable
{
    private HostClient? hostClient;
    private MainWindowViewModel? mainWindowViewModel;

    /// <inheritdoc />
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        string[] arguments = desktop.Args ?? [];
        string instanceId = GetOptionValue(arguments, "--instance-id=") ?? "default";
        bool startHostIfMissing = !arguments.Contains("--connect-existing", StringComparer.Ordinal);
        string applicationRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ExternalPeepSight");
        var preferencesStore = new UiPreferencesStore(Path.Combine(applicationRoot, "ui-preferences.json"));
        UiPreferences preferences = preferencesStore.Load();
        if (startHostIfMissing)
        {
            HostProcessManager.ClearGracefulShutdown(instanceId);
        }

        hostClient = new HostClient(
            instanceId,
            startHostIfMissing,
            preferences.ElevatedInputCompatibility);
        hostClient.HostExited += OnHostExited;

        string assetsRoot = Path.Combine(applicationRoot, "assets");
        var localization = new LocalizationService();
        localization.Apply(preferences.CultureName);
        var theme = new ThemeService();
        theme.Apply(preferences.Theme);
        var store = new AtomicConfigurationStore(Path.Combine(applicationRoot, "configuration.json"));
        ConfigurationDocument initialDocument = LoadInitialDocument(store);
        var workspace = new ConfigurationWorkspace(
            hostClient,
            store,
            assetsRoot,
            localization,
            initialDocument);
        var window = new MainWindow();
        mainWindowViewModel = new MainWindowViewModel(
            workspace,
            localization,
            new FileDialogService(window, localization),
            new MonitorEnumerationService(),
            theme,
            preferencesStore,
            preferences,
            new ScriptLibraryStore(Path.Combine(applicationRoot, "scripts.json")));
        window.DataContext = mainWindowViewModel;
        desktop.MainWindow = window;
        desktop.Exit += OnExit;
        base.OnFrameworkInitializationCompleted();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        mainWindowViewModel?.Dispose();
        mainWindowViewModel = null;
        if (hostClient is not null)
        {
            hostClient.HostExited -= OnHostExited;
            hostClient.DisposeAsync().AsTask().GetAwaiter().GetResult();
            hostClient = null;
        }

        GC.SuppressFinalize(this);
    }

    private static ConfigurationDocument LoadInitialDocument(AtomicConfigurationStore store)
    {
        try
        {
            return store.Load();
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or ConfigurationFormatException)
        {
            return ConfigurationDefaults.Create();
        }
    }

    private void OnHostExited(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        Dispose();
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
