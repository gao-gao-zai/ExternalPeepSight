using System.Collections;
using System.Globalization;
using System.Resources;
using Avalonia;

namespace ExternalPeepSight.UI.Services;

internal sealed class LocalizationService
{
    private const string ResourcePrefix = "Loc.";
    private readonly ResourceManager resourceManager =
        new("ExternalPeepSight.UI.Localization.Strings", typeof(LocalizationService).Assembly);

    public event EventHandler? CultureChanged;

    public CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("zh-CN");

    public string this[string key] =>
        resourceManager.GetString(key, Culture) ?? key;

    public void Apply(string cultureName)
    {
        CultureInfo culture = cultureName.Equals("en-US", StringComparison.OrdinalIgnoreCase)
            ? CultureInfo.GetCultureInfo("en-US")
            : CultureInfo.GetCultureInfo("zh-CN");
        Culture = culture;
        CultureInfo.CurrentUICulture = culture;

        if (Application.Current is { } application)
        {
            ResourceSet resources = resourceManager.GetResourceSet(culture, true, true)
                ?? throw new MissingManifestResourceException("Localization resources are unavailable.");
            foreach (DictionaryEntry entry in resources)
            {
                if (entry.Key is string key && entry.Value is string value)
                {
                    application.Resources[$"{ResourcePrefix}{key}"] = value;
                }
            }
        }

        CultureChanged?.Invoke(this, EventArgs.Empty);
    }
}
