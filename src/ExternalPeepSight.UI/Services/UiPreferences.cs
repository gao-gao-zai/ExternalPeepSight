using System.Text.Json;

namespace ExternalPeepSight.UI.Services;

internal enum AppTheme
{
    System,
    Light,
    Dark,
}

internal sealed record UiPreferences(AppTheme Theme, string CultureName)
{
    public static UiPreferences Default { get; } = new(AppTheme.System, "zh-CN");
}

internal sealed class UiPreferencesStore
{
    private readonly string filePath;

    public UiPreferencesStore(string filePath)
    {
        this.filePath = Path.GetFullPath(filePath);
    }

    public UiPreferences Load()
    {
        try
        {
            return File.Exists(filePath)
                ? JsonSerializer.Deserialize<UiPreferences>(File.ReadAllText(filePath)) ?? UiPreferences.Default
                : UiPreferences.Default;
        }
        catch (JsonException)
        {
            return UiPreferences.Default;
        }
    }

    public void Save(UiPreferences preferences)
    {
        string? directory = Path.GetDirectoryName(filePath);
        if (directory is null)
        {
            throw new InvalidOperationException("Preferences path has no directory.");
        }

        Directory.CreateDirectory(directory);
        File.WriteAllText(filePath, JsonSerializer.Serialize(preferences));
    }
}
