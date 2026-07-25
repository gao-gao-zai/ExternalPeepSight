using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using ExternalPeepSight.Core;

namespace ExternalPeepSight.UI.Services;

internal sealed class ConfigurationWorkspace : ObservableObject, IDisposable
{
    private readonly IHostSession hostSession;
    private readonly IHostLaunchModeSession? hostLaunchModeSession;
    private readonly AtomicConfigurationStore store;
    private readonly string assetsRoot;
    private readonly LocalizationService localization;
    private CancellationTokenSource? saveCancellation;
    private ConfigurationDocument document;
    private Guid selectedProfileSetId;
    private Guid selectedProfileId;
    private ulong nextVersion = 1;
    private bool isHostConnected;
    private string? errorMessage;
    private ConfigurationDocument lastHostAcceptedDocument;

    public ConfigurationWorkspace(
        IHostSession hostSession,
        AtomicConfigurationStore store,
        string assetsRoot,
        LocalizationService localization,
        ConfigurationDocument initialDocument)
    {
        this.hostSession = hostSession;
        hostLaunchModeSession = hostSession as IHostLaunchModeSession;
        this.store = store;
        this.assetsRoot = Path.GetFullPath(assetsRoot);
        this.localization = localization;
        document = initialDocument;
        lastHostAcceptedDocument = initialDocument;
        selectedProfileSetId = initialDocument.ActiveProfileSetId;
        selectedProfileId = ResolveSelectedProfileId(initialDocument, selectedProfileSetId);
        isHostConnected = hostSession.IsConnected;
        hostSession.StateChanged += OnHostStateChanged;
        hostSession.ConnectionChanged += OnHostConnectionChanged;
        if (hostLaunchModeSession is not null)
        {
            hostLaunchModeSession.HostLaunchFailed += OnHostLaunchFailed;
        }
        hostSession.Start();
    }

    public event EventHandler? DocumentChanged;

    public ConfigurationDocument Document
    {
        get => document;
        private set => SetProperty(ref document, value);
    }

    public Guid SelectedProfileSetId
    {
        get => selectedProfileSetId;
        private set => SetProperty(ref selectedProfileSetId, value);
    }

    public Guid SelectedProfileId
    {
        get => selectedProfileId;
        private set => SetProperty(ref selectedProfileId, value);
    }

    public Profile SelectedProfile =>
        Document.Profiles.First(profile => profile.Id == SelectedProfileId);

    public ProfileSet? SelectedProfileSet =>
        Document.ProfileSets.FirstOrDefault(profileSet => profileSet.Id == SelectedProfileSetId);

    public bool IsHostConnected
    {
        get => isHostConnected;
        private set => SetProperty(ref isHostConnected, value);
    }

    public string? ErrorMessage
    {
        get => errorMessage;
        private set => SetProperty(ref errorMessage, value);
    }

    public string AssetsRoot => assetsRoot;

    public void DismissError() => ErrorMessage = null;

    public void ReportError(string resourceKey) => ErrorMessage = localization[resourceKey];

    public async Task SetElevatedInputCompatibilityAsync(bool enabled)
    {
        if (hostLaunchModeSession is null)
        {
            return;
        }

        try
        {
            await hostLaunchModeSession.SetElevatedInputCompatibilityAsync(enabled).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or OperationCanceledException)
        {
            ApplicationLog.Write("host.compatibility_mode_restart_failed", exception);
            ErrorMessage = localization["App.HostError"];
        }
    }

    public Task<JsonElement> ValidateScriptAsync(
        JsonElement payload,
        CancellationToken cancellationToken = default)
    {
        if (hostSession is not IScriptValidationSession validationSession)
        {
            throw new InvalidOperationException("The connected Host does not support Lua script validation.");
        }

        return validationSession.ValidateScriptAsync(payload, cancellationToken);
    }

    public bool UpdateDocument(Func<ConfigurationDocument, ConfigurationDocument> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        ConfigurationDocument candidate = update(Document);
        ConfigurationValidationResult validation = ConfigurationValidator.Validate(candidate);
        if (!validation.IsValid)
        {
            ErrorMessage = localization["App.InvalidChange"];
            return false;
        }

        Publish(candidate);
        return true;
    }

    public bool UpdateSelectedProfile(Func<Profile, Profile> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        return UpdateDocument(current => current with
        {
            Profiles = current.Profiles.Select(profile =>
                profile.Id == SelectedProfileId ? update(profile) : profile).ToArray(),
        });
    }

    public void SelectProfileSet(Guid profileSetId)
    {
        ProfileSet profileSet = Document.ProfileSets.First(set => set.Id == profileSetId);
        SelectedProfileSetId = profileSetId;
        SelectedProfileId = profileSet.SelectedProfileId ?? profileSet.ProfileIds[0];
        RaiseSelectionChanged();
    }

    public void SelectProfile(Guid profileId)
    {
        if (Document.Profiles.All(profile => profile.Id != profileId))
        {
            throw new ArgumentOutOfRangeException(nameof(profileId));
        }

        ProfileSet? selectedSet = SelectedProfileSet;
        if (selectedSet is not null && selectedSet.ProfileIds.Contains(profileId))
        {
            UpdateDocument(current => current with
            {
                ProfileSets = current.ProfileSets.Select(set =>
                    set.Id == selectedSet.Id ? set with { SelectedProfileId = profileId } : set).ToArray(),
            });
            SelectedProfileId = profileId;
            RaiseSelectionChanged();
        }
        else
        {
            SelectedProfileId = profileId;
            RaiseSelectionChanged();
        }
    }

    public void ActivateProfileSet(Guid profileSetId)
    {
        UpdateDocument(current => current with { ActiveProfileSetId = profileSetId });
        SelectProfileSet(profileSetId);
    }

    public void AddProfile(string name)
    {
        Profile template = SelectedProfile;
        Guid id = Guid.NewGuid();
        Profile profile = template with { Id = id, Name = NormalizeName(name, localization["Profiles.NewProfile"]) };
        UpdateDocument(current =>
        {
            ProfileSet set = SelectedProfileSet ?? current.ProfileSets[0];
            return current with
            {
                Profiles = [.. current.Profiles, profile],
                ProfileSets = current.ProfileSets.Select(item =>
                    item.Id == set.Id
                        ? item with
                        {
                            ProfileIds = [.. item.ProfileIds, id],
                            SelectedProfileId = id,
                        }
                        : item).ToArray(),
            };
        });
        SelectedProfileId = id;
        RaiseSelectionChanged();
    }

    public void DuplicateSelectedProfile()
    {
        AddProfile($"{SelectedProfile.Name} 2");
    }

    public void DeleteSelectedProfile()
    {
        if (Document.Profiles.Length <= 1)
        {
            return;
        }

        Guid removedId = SelectedProfileId;
        Profile replacement = Document.Profiles.First(profile => profile.Id != removedId);
        UpdateDocument(current => current with
        {
            Profiles = current.Profiles.Where(profile => profile.Id != removedId).ToArray(),
            ProfileSets = current.ProfileSets.Select(set =>
            {
                Guid[] ids = set.ProfileIds.Where(id => id != removedId).ToArray();
                if (ids.Length == 0)
                {
                    ids = [replacement.Id];
                }

                return set with
                {
                    ProfileIds = ids,
                    SelectedProfileId = set.SelectedProfileId == removedId
                        ? ids[0]
                        : set.SelectedProfileId,
                };
            }).ToArray(),
        });
        SelectedProfileId = replacement.Id;
        RaiseSelectionChanged();
    }

    public void AddProfileSet(string name)
    {
        Guid id = Guid.NewGuid();
        var profileSet = new ProfileSet(
            id,
            NormalizeName(name, localization["Profiles.NewSet"]),
            [SelectedProfileId],
            SelectedProfileId);
        UpdateDocument(current => current with { ProfileSets = [.. current.ProfileSets, profileSet] });
        SelectProfileSet(id);
    }

    public void DeleteSelectedProfileSet()
    {
        if (Document.ProfileSets.Length <= 1 || SelectedProfileSet is null)
        {
            return;
        }

        Guid removedId = SelectedProfileSetId;
        ProfileSet replacement = Document.ProfileSets.First(set => set.Id != removedId);
        UpdateDocument(current => current with
        {
            ProfileSets = current.ProfileSets.Where(set => set.Id != removedId).ToArray(),
            ActiveProfileSetId = current.ActiveProfileSetId == removedId
                ? replacement.Id
                : current.ActiveProfileSetId,
        });
        SelectProfileSet(replacement.Id);
    }

    public void RenameProfile(Guid profileId, string name)
    {
        UpdateDocument(current => current with
        {
            Profiles = current.Profiles.Select(profile =>
                profile.Id == profileId ? profile with { Name = name.Trim() } : profile).ToArray(),
        });
    }

    public void RenameProfileSet(Guid profileSetId, string name)
    {
        UpdateDocument(current => current with
        {
            ProfileSets = current.ProfileSets.Select(set =>
                set.Id == profileSetId ? set with { Name = name.Trim() } : set).ToArray(),
        });
    }

    public void AssignProfile(Guid profileSetId, Guid profileId)
    {
        UpdateDocument(current => current with
        {
            ProfileSets = current.ProfileSets.Select(set =>
                set.Id == profileSetId && !set.ProfileIds.Contains(profileId)
                    ? set with { ProfileIds = [.. set.ProfileIds, profileId] }
                    : set).ToArray(),
        });
    }

    public void RemoveProfileFromSelectedSet(Guid profileId)
    {
        ProfileSet? selectedSet = SelectedProfileSet;
        if (selectedSet is null || selectedSet.ProfileIds.Length <= 1)
        {
            return;
        }

        UpdateDocument(current => current with
        {
            ProfileSets = current.ProfileSets.Select(set =>
            {
                if (set.Id != selectedSet.Id)
                {
                    return set;
                }

                Guid[] ids = set.ProfileIds.Where(id => id != profileId).ToArray();
                return set with
                {
                    ProfileIds = ids,
                    SelectedProfileId = set.SelectedProfileId == profileId
                        ? ids[0]
                        : set.SelectedProfileId,
                };
            }).ToArray(),
        });
    }

    public AssetReference ImportImage(string sourcePath)
    {
        byte[] content = File.ReadAllBytes(sourcePath);
        if (content.Length is < 1 or > 10 * 1024 * 1024)
        {
            throw new ConfigurationFormatException("Image size is outside the supported range.");
        }

        string extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        string mediaType = extension switch
        {
            ".png" when IsPng(content) => "image/png",
            ".svg" when IsStaticSvg(content) => "image/svg+xml",
            _ => throw new ConfigurationFormatException("Image format is unsupported."),
        };
        Guid id = Guid.NewGuid();
        string fileName = $"{id:N}{extension}";
        string hash = Convert.ToHexString(SHA256.HashData(content));
        var reference = new AssetReference(id, fileName, mediaType, content.LongLength, hash);

        Directory.CreateDirectory(assetsRoot);
        string destination = Path.Combine(assetsRoot, fileName);
        File.WriteAllBytes(destination, content);
        try
        {
            if (!UpdateDocument(current => current with { Assets = [.. current.Assets, reference] }))
            {
                throw new ConfigurationValidationException(
                    [new ValidationIssue("$.assets", "Imported image metadata is invalid.")]);
            }
        }
        catch
        {
            File.Delete(destination);
            throw;
        }

        return reference;
    }

    public void ApplyImport(ConfigurationMergeResult mergeResult)
    {
        ArgumentNullException.ThrowIfNull(mergeResult);
        Directory.CreateDirectory(assetsRoot);
        var written = new List<string>();
        try
        {
            foreach (EpsxAsset asset in mergeResult.Assets)
            {
                string path = Path.Combine(assetsRoot, asset.Reference.FileName);
                File.WriteAllBytes(path, asset.Content);
                written.Add(path);
            }

            if (!UpdateDocument(_ => mergeResult.Document))
            {
                throw new ConfigurationValidationException(
                    [new ValidationIssue("$", "Imported configuration is invalid.")]);
            }
        }
        catch
        {
            foreach (string path in written)
            {
                File.Delete(path);
            }

            throw;
        }
    }

    public EpsxAsset[] ReadAssets()
    {
        return Document.Assets.Select(reference =>
        {
            string path = Path.Combine(assetsRoot, reference.FileName);
            return new EpsxAsset(reference, File.ReadAllBytes(path));
        }).ToArray();
    }

    public void Dispose()
    {
        hostSession.StateChanged -= OnHostStateChanged;
        hostSession.ConnectionChanged -= OnHostConnectionChanged;
        if (hostLaunchModeSession is not null)
        {
            hostLaunchModeSession.HostLaunchFailed -= OnHostLaunchFailed;
        }
        saveCancellation?.Cancel();
        saveCancellation?.Dispose();
        try
        {
            store.Save(Document);
        }
        catch (IOException)
        {
        }
    }

    private void Publish(ConfigurationDocument candidate)
    {
        Document = candidate;
        EnsureSelection();
        ErrorMessage = null;
        DocumentChanged?.Invoke(this, EventArgs.Empty);
        QueueHostSnapshot(candidate);
        QueueSave(candidate);
    }

    private void EnsureSelection()
    {
        if (Document.ProfileSets.All(set => set.Id != SelectedProfileSetId))
        {
            SelectedProfileSetId = Document.ProfileSets.FirstOrDefault()?.Id ?? Guid.Empty;
        }

        if (Document.Profiles.All(profile => profile.Id != SelectedProfileId))
        {
            SelectedProfileId = ResolveSelectedProfileId(Document, SelectedProfileSetId);
        }
    }

    private void RaiseSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedProfile));
        OnPropertyChanged(nameof(SelectedProfileSet));
        DocumentChanged?.Invoke(this, EventArgs.Empty);
    }

    private void QueueHostSnapshot(ConfigurationDocument candidate)
    {
        ulong version = nextVersion++;
        using JsonDocument json = JsonDocument.Parse(ConfigurationJson.Serialize(candidate));
        Task task = hostSession.QueueSnapshotAsync(version, json.RootElement.Clone());
        _ = ObserveHostUpdateAsync(task, candidate);
    }

    private async Task ObserveHostUpdateAsync(Task task, ConfigurationDocument candidate)
    {
        try
        {
            await task.ConfigureAwait(false);
            lastHostAcceptedDocument = candidate;
        }
        catch (HostRequestException exception)
        {
            if (ReferenceEquals(Document, candidate))
            {
                Document = lastHostAcceptedDocument;
                EnsureSelection();
                DocumentChanged?.Invoke(this, EventArgs.Empty);
                QueueSave(lastHostAcceptedDocument);
            }

            ApplicationLog.Write("host.snapshot_rejected", exception);
            ErrorMessage = string.Format(
                localization.Culture,
                localization["App.HostRequestError"],
                exception.Code,
                exception.HostMessage);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or OperationCanceledException)
        {
            ApplicationLog.Write("host.snapshot_update_failed", exception);
            ErrorMessage = localization["App.HostError"];
        }
    }

    private void QueueSave(ConfigurationDocument candidate)
    {
        saveCancellation?.Cancel();
        saveCancellation?.Dispose();
        saveCancellation = new CancellationTokenSource();
        _ = SaveAsync(candidate, saveCancellation.Token);
    }

    private async Task SaveAsync(ConfigurationDocument candidate, CancellationToken cancellationToken)
    {
        try
        {
            await store.SaveDebouncedAsync(candidate, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
            ErrorMessage = localization["App.PersistenceError"];
        }
    }

    private void OnHostConnectionChanged(object? sender, bool connected)
    {
        IsHostConnected = connected;
    }

    private void OnHostLaunchFailed(object? sender, HostLaunchFailure failure)
    {
        ErrorMessage = failure switch
        {
            HostLaunchFailure.ElevationCancelled => localization["App.HostElevationCancelled"],
            _ => localization["App.HostError"],
        };
    }

    private void OnHostStateChanged(object? sender, JsonElement state)
    {
        if (state.ValueKind != JsonValueKind.Object ||
            !state.TryGetProperty("configurationVersion", out JsonElement versionElement) ||
            !versionElement.TryGetUInt64(out ulong version))
        {
            return;
        }

        nextVersion = Math.Max(nextVersion, version + 1);
        if (!state.TryGetProperty("snapshot", out JsonElement snapshot) ||
            snapshot.ValueKind == JsonValueKind.Null)
        {
            QueueHostSnapshot(Document);
            return;
        }

        try
        {
            ConfigurationDocument hostDocument = ConfigurationJson.Deserialize(snapshot.GetRawText());
            Document = hostDocument;
            lastHostAcceptedDocument = hostDocument;
            EnsureSelection();
            DocumentChanged?.Invoke(this, EventArgs.Empty);
            QueueSave(hostDocument);
        }
        catch (Exception exception) when (
            exception is ConfigurationFormatException or ConfigurationValidationException)
        {
            ErrorMessage = localization["App.HostError"];
        }
    }

    private static Guid ResolveSelectedProfileId(ConfigurationDocument source, Guid profileSetId)
    {
        ProfileSet? profileSet = source.ProfileSets.FirstOrDefault(set => set.Id == profileSetId);
        return profileSet?.SelectedProfileId ??
            profileSet?.ProfileIds.FirstOrDefault() ??
            source.Profiles[0].Id;
    }

    private static string NormalizeName(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static bool IsPng(ReadOnlySpan<byte> content) =>
        content.Length >= 8 &&
        content[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

    private static bool IsStaticSvg(byte[] content)
    {
        string text;
        try
        {
            text = Encoding.UTF8.GetString(content);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        return text.Contains("<svg", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("<script", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("javascript:", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("http://", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("https://", StringComparison.OrdinalIgnoreCase);
    }
}
