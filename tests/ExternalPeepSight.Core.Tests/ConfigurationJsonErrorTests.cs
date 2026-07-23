namespace ExternalPeepSight.Core.Tests;

public sealed class ConfigurationJsonErrorTests
{
    [Theory]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("{\"schemaVersion\":-1}")]
    [InlineData("{\"schemaVersion\":\"one\"}")]
    public void InvalidRootOrVersionIsRejected(string json)
    {
        Assert.Throws<ConfigurationFormatException>(() => ConfigurationJson.Deserialize(json));
    }

    [Fact]
    public void InvalidColorRepresentationsAreRejected()
    {
        string json = ConfigurationJson.Serialize(ConfigurationDefaults.Create());
        string invalidHex = json.Replace("#FFFFFFFF", "#INVALID", StringComparison.Ordinal);
        string nonString = json.Replace("\"#FFFFFFFF\"", "42", StringComparison.Ordinal);

        Assert.Throws<ConfigurationFormatException>(() => ConfigurationJson.Deserialize(invalidHex));
        Assert.Throws<ConfigurationFormatException>(() => ConfigurationJson.Deserialize(nonString));
    }

    [Fact]
    public void SerializeRejectsInvalidDocument()
    {
        ConfigurationDocument invalid = ConfigurationDefaults.Create() with
        {
            Toasts = ConfigurationDefaults.Create().Toasts with { DurationMs = 0 },
        };

        Assert.Throws<ConfigurationValidationException>(() => ConfigurationJson.Serialize(invalid));
    }

    [Fact]
    public void VersionZeroAddsMissingCollections()
    {
        const string json = """
            {
              "monitorSelection": {
                "mode": "focus",
                "monitorIds": [],
                "focusSource": "foregroundWindowThenMouse"
              },
              "switches": {
                "visibilityRule": "switchA",
                "initialStateA": false,
                "initialStateB": false,
                "switchA": {
                  "mode": "unbound",
                  "toggleKey": null,
                  "enableKey": null,
                  "disableKey": null,
                  "holdKey": null
                },
                "switchB": {
                  "mode": "unbound",
                  "toggleKey": null,
                  "enableKey": null,
                  "disableKey": null,
                  "holdKey": null
                }
              },
              "toasts": {
                "enabled": true,
                "position": "topCenter",
                "durationMs": 1500,
                "fontFamily": "Segoe UI",
                "fontSizePx": 18,
                "foreground": "#FFFFFFFF",
                "background": "#000000B4"
              }
            }
            """;

        ConfigurationDocument document = ConfigurationJson.Deserialize(json);

        Assert.Empty(document.Profiles);
        Assert.Empty(document.ProfileSets);
        Assert.Empty(document.Assets);
    }
}
