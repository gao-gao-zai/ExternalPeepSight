using Avalonia;
using Avalonia.Headless;

namespace ExternalPeepSight.UI.Tests;

public static class AvaloniaTestSetup
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
