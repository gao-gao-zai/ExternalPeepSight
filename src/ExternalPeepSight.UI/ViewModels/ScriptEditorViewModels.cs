using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExternalPeepSight.Core;
using ExternalPeepSight.UI.Services;

namespace ExternalPeepSight.UI.ViewModels;

internal sealed class ScriptBindingEditorViewModel : ObservableObject
{
    private readonly Action changed;
    private bool enabled;
    private KeyIdentity? key;

    public ScriptBindingEditorViewModel(
        ScriptBindingSlot binding,
        Action changed)
    {
        Id = binding.Id;
        DisplayName = binding.DisplayName;
        Pressed = binding.Pressed;
        Released = binding.Released;
        enabled = binding.Enabled;
        key = binding.Key;
        this.changed = changed;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public bool Pressed { get; }

    public bool Released { get; }

    public bool Enabled
    {
        get => enabled;
        set
        {
            if (SetProperty(ref enabled, value))
            {
                changed();
            }
        }
    }

    public KeyIdentity? Key
    {
        get => key;
        set
        {
            if (SetProperty(ref key, value))
            {
                changed();
            }
        }
    }

    public ScriptBindingSlot ToModel() =>
        new(Id, DisplayName, Pressed, Released, Enabled, Key);
}

internal sealed class ScriptSettingEditorViewModel : ObservableObject
{
    private readonly Action<ScriptSettingEditorViewModel> changed;
    private readonly string groupName;
    private string value;
    private bool visible = true;

    public ScriptSettingEditorViewModel(
        ScriptSetting setting,
        ScriptUiItem? presentation,
        string groupPrefix,
        Action<ScriptSettingEditorViewModel> changed)
    {
        Id = setting.Id;
        DisplayName = setting.DisplayName;
        Type = setting.Type;
        value = setting.Value;
        Options = setting.Options;
        Minimum = setting.Minimum;
        Maximum = setting.Maximum;
        Control = ResolveControl(setting.Type, presentation?.Control ?? ScriptUiControlType.Auto);
        Description = presentation?.Description ?? string.Empty;
        Unit = presentation?.Unit ?? string.Empty;
        Step = presentation?.Step ?? (setting.Type == ScriptSettingType.Integer ? 1 : 0.1);
        VisibleWhen = presentation?.VisibleWhen;
        groupName = $"{groupPrefix}-{setting.Id}";
        this.changed = changed;
        SegmentedOptions = Options
            .Select(option => new ScriptSettingOptionViewModel(this, option))
            .ToArray();
    }

    public string Id { get; }

    public string DisplayName { get; }

    public ScriptSettingType Type { get; }

    public string Value
    {
        get => value;
        set
        {
            if (SetProperty(ref this.value, value))
            {
                OnPropertyChanged(nameof(BooleanValue));
                OnPropertyChanged(nameof(NumericValue));
                foreach (ScriptSettingOptionViewModel option in SegmentedOptions)
                {
                    option.Refresh();
                }
                changed(this);
            }
        }
    }

    public IReadOnlyList<string> Options { get; }

    public IReadOnlyList<ScriptSettingOptionViewModel> SegmentedOptions { get; }

    public double? Minimum { get; }

    public double? Maximum { get; }

    public ScriptUiControlType Control { get; }

    public string Description { get; }

    public string Unit { get; }

    public double Step { get; }

    public ScriptUiVisibilityCondition? VisibleWhen { get; }

    public string GroupName => groupName;

    public bool HasDescription => !string.IsNullOrEmpty(Description);

    public bool HasUnit => !string.IsNullOrEmpty(Unit);

    public bool UsesSwitch => Control == ScriptUiControlType.Switch;

    public bool UsesCheckbox => Control == ScriptUiControlType.Checkbox;

    public bool UsesSlider => Control == ScriptUiControlType.Slider;

    public bool UsesNumber => Control == ScriptUiControlType.Number;

    public bool UsesTextbox => Control == ScriptUiControlType.Textbox;

    public bool UsesSelect => Control == ScriptUiControlType.Select;

    public bool UsesSegmented => Control == ScriptUiControlType.Segmented;

    public double NumericMinimum => Minimum ?? double.NegativeInfinity;

    public double NumericMaximum => Maximum ?? double.PositiveInfinity;

    public bool IsVisible
    {
        get => visible;
        private set => SetProperty(ref visible, value);
    }

    public bool BooleanValue
    {
        get => bool.TryParse(Value, out bool parsed) && parsed;
        set => Value = value ? "true" : "false";
    }

    public double NumericValue
    {
        get => double.TryParse(
            Value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double parsed)
            ? parsed
            : 0;
        set => Value = Type == ScriptSettingType.Integer
            ? Math.Round(value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("G17", CultureInfo.InvariantCulture);
    }

    public void UpdateVisibility(IReadOnlyDictionary<string, ScriptSettingEditorViewModel> settings)
    {
        IsVisible = VisibleWhen is null ||
            settings.TryGetValue(VisibleWhen.SettingId, out ScriptSettingEditorViewModel? source) &&
            string.Equals(source.Value, VisibleWhen.EqualsValue, StringComparison.Ordinal);
    }

    public ScriptSetting ToModel() =>
        new(Id, DisplayName, Type, Value, Options.ToArray(), Minimum, Maximum);

    private static ScriptUiControlType ResolveControl(
        ScriptSettingType settingType,
        ScriptUiControlType requested)
    {
        if (requested != ScriptUiControlType.Auto)
        {
            return requested;
        }

        return settingType switch
        {
            ScriptSettingType.Boolean => ScriptUiControlType.Switch,
            ScriptSettingType.Integer or ScriptSettingType.Double => ScriptUiControlType.Number,
            ScriptSettingType.String => ScriptUiControlType.Textbox,
            ScriptSettingType.Enum => ScriptUiControlType.Select,
            _ => ScriptUiControlType.Textbox,
        };
    }
}

internal sealed class ScriptSettingOptionViewModel : ObservableObject
{
    private readonly ScriptSettingEditorViewModel owner;

    public ScriptSettingOptionViewModel(
        ScriptSettingEditorViewModel owner,
        string value)
    {
        this.owner = owner;
        Value = value;
    }

    public string Value { get; }

    public string GroupName => owner.GroupName;

    public bool IsSelected
    {
        get => string.Equals(owner.Value, Value, StringComparison.Ordinal);
        set
        {
            if (value)
            {
                owner.Value = Value;
            }
        }
    }

    public void Refresh() => OnPropertyChanged(nameof(IsSelected));
}

internal sealed class ScriptUiSettingRowViewModel
{
    public ScriptUiSettingRowViewModel(
        ScriptSettingEditorViewModel first,
        ScriptSettingEditorViewModel? second)
    {
        First = first;
        Second = second;
    }

    public ScriptSettingEditorViewModel First { get; }

    public ScriptSettingEditorViewModel? Second { get; }

    public bool HasSecond => Second is not null;
}

internal sealed class ScriptUiSectionEditorViewModel : ObservableObject
{
    private readonly IReadOnlyList<ScriptSettingEditorViewModel> settings;

    public ScriptUiSectionEditorViewModel(
        ScriptUiSection section,
        IReadOnlyDictionary<string, ScriptSettingEditorViewModel> settingLookup)
    {
        Id = section.Id;
        DisplayName = section.DisplayName;
        Description = section.Description;
        Collapsible = section.Collapsible;
        IsExpanded = !section.Collapsible || section.DefaultExpanded;
        Columns = section.Columns;
        settings = section.Items.Select(item => settingLookup[item.SettingId]).ToArray();
        Rows = [];
        RefreshRows();
    }

    public string Id { get; }

    public string DisplayName { get; }

    public string Description { get; }

    public bool Collapsible { get; }

    public bool IsExpanded { get; set; }

    public int Columns { get; }

    public bool HasDescription => !string.IsNullOrEmpty(Description);

    public bool HasVisibleItems => Rows.Count > 0;

    public ObservableCollection<ScriptUiSettingRowViewModel> Rows { get; }

    public void RefreshRows()
    {
        Rows.Clear();
        ScriptSettingEditorViewModel[] visibleSettings = settings
            .Where(setting => setting.IsVisible)
            .ToArray();
        int stride = Columns == 2 ? 2 : 1;
        for (int index = 0; index < visibleSettings.Length; index += stride)
        {
            Rows.Add(new(
                visibleSettings[index],
                stride == 2 && index + 1 < visibleSettings.Length
                    ? visibleSettings[index + 1]
                    : null));
        }
        OnPropertyChanged(nameof(HasVisibleItems));
    }
}

internal sealed class ScriptAdvancedItemViewModel : ObservableObject
{
    private readonly ConfigurationWorkspace workspace;
    private readonly Guid scriptId;
    private readonly Action beginPersist;
    private readonly Action endPersist;

    public ScriptAdvancedItemViewModel(
        ConfigurationWorkspace workspace,
        ScriptConfiguration script,
        Action beginPersist,
        Action endPersist)
    {
        this.workspace = workspace;
        scriptId = script.Id;
        this.beginPersist = beginPersist;
        this.endPersist = endPersist;
        Name = script.Name;
        IsEnabled = script.Enabled;
        Bindings = [];
        Settings = [];
        Sections = [];
        Load(script);
    }

    public string Name { get; }

    public bool IsEnabled { get; }

    public ObservableCollection<ScriptBindingEditorViewModel> Bindings { get; }

    public ObservableCollection<ScriptSettingEditorViewModel> Settings { get; }

    public ObservableCollection<ScriptUiSectionEditorViewModel> Sections { get; }

    public bool HasBindings => Bindings.Count > 0;

    public bool HasSettings => Settings.Count > 0;

    public bool HasCustomUi => Sections.Count > 0;

    public bool UsesAutomaticUi => HasSettings && !HasCustomUi;

    public bool HasNoDeclarations => !HasBindings && !HasSettings;

    private void Load(ScriptConfiguration script)
    {
        Bindings.Clear();
        Settings.Clear();
        Sections.Clear();
        foreach (ScriptBindingSlot binding in script.Bindings)
        {
            Bindings.Add(new(binding, PersistDeclaredValues));
        }

        Dictionary<string, ScriptUiItem> presentations = script.Ui?.Sections
            .SelectMany(section => section.Items)
            .ToDictionary(item => item.SettingId, StringComparer.Ordinal) ?? [];
        foreach (ScriptSetting setting in script.Settings)
        {
            presentations.TryGetValue(setting.Id, out ScriptUiItem? presentation);
            Settings.Add(new(setting, presentation, $"script-setting-{scriptId:N}", OnSettingChanged));
        }

        var settingLookup = Settings.ToDictionary(setting => setting.Id, StringComparer.Ordinal);
        UpdateConditionalVisibility(settingLookup);
        if (script.Ui is not null)
        {
            foreach (ScriptUiSection section in script.Ui.Sections)
            {
                Sections.Add(new(section, settingLookup));
            }
        }

        OnPropertyChanged(nameof(HasBindings));
        OnPropertyChanged(nameof(HasSettings));
        OnPropertyChanged(nameof(HasCustomUi));
        OnPropertyChanged(nameof(UsesAutomaticUi));
        OnPropertyChanged(nameof(HasNoDeclarations));
    }

    private void OnSettingChanged(ScriptSettingEditorViewModel _)
    {
        Dictionary<string, ScriptSettingEditorViewModel> settingLookup =
            Settings.ToDictionary(setting => setting.Id, StringComparer.Ordinal);
        UpdateConditionalVisibility(settingLookup);
        foreach (ScriptUiSectionEditorViewModel section in Sections)
        {
            section.RefreshRows();
        }
        PersistDeclaredValues();
    }

    private void UpdateConditionalVisibility(
        IReadOnlyDictionary<string, ScriptSettingEditorViewModel> settingLookup)
    {
        foreach (ScriptSettingEditorViewModel setting in Settings)
        {
            setting.UpdateVisibility(settingLookup);
        }
    }

    private void PersistDeclaredValues()
    {
        beginPersist();
        try
        {
            bool updated = workspace.UpdateSelectedProfile(profile =>
            {
                int scriptIndex = Array.FindIndex(profile.Scripts, script => script.Id == scriptId);
                if (scriptIndex < 0)
                {
                    return profile;
                }

                ScriptConfiguration[] scripts = profile.Scripts.ToArray();
                scripts[scriptIndex] = scripts[scriptIndex] with
                {
                    Bindings = Bindings.Select(binding => binding.ToModel()).ToArray(),
                    Settings = Settings.Select(setting => setting.ToModel()).ToArray(),
                };
                return profile with
                {
                    Scripts = scripts,
                };
            });
            if (!updated)
            {
                ScriptConfiguration? current = workspace.SelectedProfile.Scripts
                    .FirstOrDefault(script => script.Id == scriptId);
                if (current is not null)
                {
                    Load(current);
                }
            }
        }
        finally
        {
            endPersist();
        }
    }
}

internal sealed class ScriptAdvancedSummaryViewModel : WorkspaceViewModel
{
    private static readonly ObservableCollection<ScriptBindingEditorViewModel> EmptyBindings = [];
    private static readonly ObservableCollection<ScriptSettingEditorViewModel> EmptySettings = [];
    private static readonly ObservableCollection<ScriptUiSectionEditorViewModel> EmptySections = [];
    private bool persistingValues;

    public ScriptAdvancedSummaryViewModel(
        ConfigurationWorkspace workspace,
        Action openScriptManager)
        : base(workspace)
    {
        ArgumentNullException.ThrowIfNull(openScriptManager);
        Scripts = [];
        OpenScriptManagerCommand = new RelayCommand(openScriptManager);
        LoadFromDocument();
    }

    public ObservableCollection<ScriptAdvancedItemViewModel> Scripts { get; }

    public bool HasScript => Scripts.Count > 0;

    public ObservableCollection<ScriptBindingEditorViewModel> Bindings =>
        Scripts.FirstOrDefault()?.Bindings ?? EmptyBindings;

    public ObservableCollection<ScriptSettingEditorViewModel> Settings =>
        Scripts.FirstOrDefault()?.Settings ?? EmptySettings;

    public ObservableCollection<ScriptUiSectionEditorViewModel> Sections =>
        Scripts.FirstOrDefault()?.Sections ?? EmptySections;

    public bool HasCustomUi => Scripts.FirstOrDefault()?.HasCustomUi == true;

    public IRelayCommand OpenScriptManagerCommand { get; }

    protected override void Refresh()
    {
        if (!persistingValues)
        {
            LoadFromDocument();
        }
    }

    private void LoadFromDocument()
    {
        Scripts.Clear();
        foreach (ScriptConfiguration script in Profile.Scripts)
        {
            Scripts.Add(new(
                Workspace,
                script,
                () => persistingValues = true,
                () => persistingValues = false));
        }
        OnPropertyChanged(nameof(HasScript));
        OnPropertyChanged(nameof(Bindings));
        OnPropertyChanged(nameof(Settings));
        OnPropertyChanged(nameof(Sections));
        OnPropertyChanged(nameof(HasCustomUi));
    }
}

internal sealed class ScriptLibraryItemViewModel
{
    public ScriptLibraryItemViewModel(ScriptLibraryEntry entry)
    {
        Id = entry.Id;
        Name = entry.Name;
    }

    public Guid Id { get; }

    public string Name { get; }
}

internal sealed class ScriptAssignmentItemViewModel
{
    public ScriptAssignmentItemViewModel(ScriptConfiguration script)
    {
        Id = script.Id;
        Name = script.Name;
        Enabled = script.Enabled;
    }

    public Guid Id { get; }

    public string Name { get; }

    public bool Enabled { get; }
}

internal sealed class ScriptManagerViewModel : WorkspaceViewModel, IDisposable
{
    private const string NewScriptTemplate = "return eps.script {\n}\n";
    private readonly LocalizationService localization;
    private readonly IScriptLibraryStore libraryStore;
    private readonly List<ScriptLibraryEntry> allScripts;
    private ScriptScope scope;
    private ScriptLibraryItemViewModel? selectedLibrary;
    private ScriptAssignmentItemViewModel? selectedAssignment;
    private bool assignmentSelected = true;
    private bool creatingAssignment;
    private string name = string.Empty;
    private string source = string.Empty;
    private string? statusText;
    private bool hasError;
    private bool busy;

    public ScriptManagerViewModel(
        ConfigurationWorkspace workspace,
        LocalizationService localization,
        IScriptLibraryStore libraryStore)
        : base(workspace)
    {
        this.localization = localization;
        this.libraryStore = libraryStore;
        allScripts = libraryStore.Load().ToList();
        ScopeOptions =
        [
            new(ScriptScope.Profile, "Script.ScopeProfile", localization),
            new(ScriptScope.ProfileSet, "Script.ScopeProfileSet", localization),
            new(ScriptScope.Global, "Script.ScopeGlobal", localization),
        ];
        AttachedScripts = [];
        LibraryScripts = [];
        NewAssignmentCommand = new RelayCommand(NewAssignment);
        DeleteAssignmentCommand = new RelayCommand(DeleteAssignment);
        NewScriptCommand = new RelayCommand(CreateScript);
        SaveScriptCommand = new RelayCommand(SaveSelectedLibrary);
        DeleteScriptCommand = new RelayCommand(DeleteSelectedLibrary);
        SaveAssignmentToLibraryCommand = new RelayCommand(SaveAssignmentToLibrary);
        ValidateAndApplyCommand = new AsyncRelayCommand(ValidateAndApplyAsync);
        CopyToTargetCommand = new AsyncRelayCommand(CopyToTargetAsync);
        DisableAssignmentCommand = new RelayCommand(DisableAssignment);
        RefreshLibrary();
        RefreshAssignments();
        SelectDefaultAssignment();
        localization.CultureChanged += OnCultureChanged;
    }

    public IReadOnlyList<LocalizedOption<ScriptScope>> ScopeOptions { get; }

    public ObservableCollection<ScriptAssignmentItemViewModel> AttachedScripts { get; }

    public ObservableCollection<ScriptLibraryItemViewModel> LibraryScripts { get; }

    public bool HasAttachedScripts => AttachedScripts.Count > 0;

    public bool HasLibraryScripts => LibraryScripts.Count > 0;

    public ScriptScope Scope
    {
        get => scope;
        set
        {
            if (SetProperty(ref scope, value))
            {
                OnPropertyChanged(nameof(ScopeIndex));
                OnPropertyChanged(nameof(IsProfileScope));
                OnPropertyChanged(nameof(IsProfileSetScope));
                OnPropertyChanged(nameof(IsGlobalScope));
                RefreshLibrary();
                RefreshAssignments();
                SelectDefaultAssignment();
            }
        }
    }

    public int ScopeIndex
    {
        get => (int)Scope;
        set
        {
            if (Enum.IsDefined((ScriptScope)value))
            {
                Scope = (ScriptScope)value;
            }
        }
    }

    public bool IsProfileScope
    {
        get => Scope == ScriptScope.Profile;
        set
        {
            if (value)
            {
                Scope = ScriptScope.Profile;
            }
        }
    }

    public bool IsProfileSetScope
    {
        get => Scope == ScriptScope.ProfileSet;
        set
        {
            if (value)
            {
                Scope = ScriptScope.ProfileSet;
            }
        }
    }

    public bool IsGlobalScope
    {
        get => Scope == ScriptScope.Global;
        set
        {
            if (value)
            {
                Scope = ScriptScope.Global;
            }
        }
    }

    public ScriptLibraryItemViewModel? SelectedLibrary
    {
        get => selectedLibrary;
        set
        {
            if (value is not null && SetProperty(ref selectedLibrary, value))
            {
                selectedAssignment = null;
                creatingAssignment = false;
                assignmentSelected = false;
                OnPropertyChanged(nameof(SelectedAssignment));
                LoadSelectedLibrary();
                NotifyEditorMode();
            }
        }
    }

    public ScriptAssignmentItemViewModel? SelectedAssignment
    {
        get => selectedAssignment;
        set
        {
            if (value is not null && SetProperty(ref selectedAssignment, value))
            {
                selectedLibrary = null;
                creatingAssignment = false;
                assignmentSelected = true;
                OnPropertyChanged(nameof(SelectedLibrary));
                LoadAssignment();
                NotifyEditorMode();
            }
        }
    }

    public bool IsAssignmentSelected =>
        assignmentSelected && (creatingAssignment || SelectedAssignment is not null);

    public bool IsLibrarySelected => !assignmentSelected && SelectedLibrary is not null;

    public bool AssignmentHasScript => SelectedAssignment is not null;

    public bool AssignmentEnabled => CurrentScript?.Enabled == true;

    public string CurrentTargetName => Scope switch
    {
        ScriptScope.Profile => Workspace.SelectedProfile.Name,
        ScriptScope.ProfileSet => Workspace.SelectedProfileSet?.Name ?? localization["Script.NoProfileSet"],
        ScriptScope.Global => localization["Script.GlobalTarget"],
        _ => string.Empty,
    };

    public string Name
    {
        get => name;
        set
        {
            if (SetProperty(ref name, value))
            {
                ClearStatus();
            }
        }
    }

    public string Source
    {
        get => source;
        set
        {
            if (SetProperty(ref source, value))
            {
                ClearStatus();
            }
        }
    }

    public string? StatusText
    {
        get => statusText;
        private set => SetProperty(ref statusText, value);
    }

    public bool HasError
    {
        get => hasError;
        private set => SetProperty(ref hasError, value);
    }

    public bool IsBusy
    {
        get => busy;
        private set
        {
            if (SetProperty(ref busy, value))
            {
                ValidateAndApplyCommand.NotifyCanExecuteChanged();
                CopyToTargetCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public IRelayCommand NewAssignmentCommand { get; }

    public IRelayCommand DeleteAssignmentCommand { get; }

    public IRelayCommand NewScriptCommand { get; }

    public IRelayCommand SaveScriptCommand { get; }

    public IRelayCommand DeleteScriptCommand { get; }

    public IRelayCommand SaveAssignmentToLibraryCommand { get; }

    public IAsyncRelayCommand ValidateAndApplyCommand { get; }

    public IAsyncRelayCommand CopyToTargetCommand { get; }

    public IRelayCommand DisableAssignmentCommand { get; }

    public void OpenProfileAssignment()
    {
        Scope = ScriptScope.Profile;
        RefreshAssignments();
        SelectDefaultAssignment();
    }

    public void Dispose()
    {
        localization.CultureChanged -= OnCultureChanged;
    }

    protected override void Refresh()
    {
        OnPropertyChanged(nameof(CurrentTargetName));
        OnPropertyChanged(nameof(AssignmentHasScript));
        OnPropertyChanged(nameof(AssignmentEnabled));
        Guid? selectedId = SelectedAssignment?.Id;
        RefreshAssignments(selectedId);
        if (assignmentSelected)
        {
            if (selectedId.HasValue && SelectedAssignment is not null)
            {
                LoadAssignment();
            }
            else
            {
                SelectDefaultAssignment();
            }
        }
    }

    private void SelectDefaultAssignment()
    {
        ScriptAssignmentItemViewModel? first = AttachedScripts.FirstOrDefault();
        if (first is null)
        {
            NewAssignment();
            return;
        }

        selectedAssignment = null;
        SelectedAssignment = first;
    }

    private void NewAssignment()
    {
        selectedLibrary = null;
        selectedAssignment = null;
        assignmentSelected = true;
        creatingAssignment = true;
        name = GetNewAssignmentName();
        source = NewScriptTemplate;
        ClearStatus();
        OnPropertyChanged(nameof(SelectedLibrary));
        OnPropertyChanged(nameof(SelectedAssignment));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Source));
        NotifyEditorMode();
    }

    private void LoadAssignment()
    {
        ScriptConfiguration? script = CurrentScript;
        if (script is null)
        {
            NewAssignment();
            return;
        }

        name = script.Name;
        source = script?.Source ?? string.Empty;
        ClearStatus();
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Source));
        OnPropertyChanged(nameof(CurrentTargetName));
        OnPropertyChanged(nameof(AssignmentHasScript));
        OnPropertyChanged(nameof(AssignmentEnabled));
    }

    private void LoadSelectedLibrary()
    {
        ScriptLibraryEntry? script = SelectedLibrary is null
            ? null
            : allScripts.FirstOrDefault(item => item.Id == SelectedLibrary.Id);
        if (script is null)
        {
            SelectDefaultAssignment();
            return;
        }

        name = script.Name;
        source = script.Source;
        ClearStatus();
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Source));
    }

    private void CreateScript()
    {
        string baseName = localization["Script.NewScript"];
        string candidate = baseName;
        int suffix = 2;
        while (allScripts.Any(script =>
            script.Scope == Scope &&
            string.Equals(script.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{baseName} {suffix++}";
        }

        var script = new ScriptLibraryEntry(Guid.NewGuid(), candidate, Scope, NewScriptTemplate);
        allScripts.Add(script);
        if (!TrySaveLibrary())
        {
            allScripts.Remove(script);
            return;
        }

        RefreshLibrary(script.Id);
        SetStatus(localization["Script.LibraryCreated"], error: false);
    }

    private void SaveSelectedLibrary()
    {
        if (SelectedLibrary is null)
        {
            return;
        }

        int index = allScripts.FindIndex(script => script.Id == SelectedLibrary.Id);
        if (index < 0)
        {
            return;
        }

        ScriptLibraryEntry previous = allScripts[index];
        allScripts[index] = previous with
        {
            Name = Name.Trim(),
            Source = Source,
        };
        if (!TrySaveLibrary())
        {
            allScripts[index] = previous;
            return;
        }

        RefreshLibrary(previous.Id);
        SetStatus(localization["Script.LibrarySaved"], error: false);
    }

    private void DeleteSelectedLibrary()
    {
        if (SelectedLibrary is null)
        {
            return;
        }

        ScriptLibraryEntry? removed = allScripts.FirstOrDefault(script => script.Id == SelectedLibrary.Id);
        if (removed is null)
        {
            return;
        }

        allScripts.Remove(removed);
        if (!TrySaveLibrary())
        {
            allScripts.Add(removed);
            return;
        }

        RefreshLibrary();
        SelectDefaultAssignment();
        SetStatus(localization["Script.LibraryDeleted"], error: false);
    }

    private void SaveAssignmentToLibrary()
    {
        if (string.IsNullOrWhiteSpace(Source))
        {
            SetStatus(localization["Script.SourceRequired"], error: true);
            return;
        }

        var script = new ScriptLibraryEntry(
            Guid.NewGuid(),
            string.IsNullOrWhiteSpace(Name) ? CurrentTargetName : Name.Trim(),
            Scope,
            Source);
        allScripts.Add(script);
        if (!TrySaveLibrary())
        {
            allScripts.Remove(script);
            return;
        }

        RefreshLibrary(script.Id);
        SetStatus(localization["Script.AssignmentSavedToLibrary"], error: false);
    }

    private async Task ValidateAndApplyAsync()
    {
        if (!IsAssignmentSelected)
        {
            return;
        }

        await ApplySourceAsync(Source, localization["Script.ValidationSucceeded"]);
    }

    private async Task CopyToTargetAsync()
    {
        if (!IsLibrarySelected)
        {
            return;
        }

        SaveSelectedLibrary();
        if (HasError)
        {
            return;
        }

        await ApplySourceAsync(Source, localization["Script.CopiedToTarget"]);
    }

    private async Task ApplySourceAsync(string scriptSource, string successMessage)
    {
        if (string.IsNullOrWhiteSpace(scriptSource))
        {
            SetStatus(localization["Script.SourceRequired"], error: true);
            return;
        }

        ScriptTarget target = CurrentTarget;
        ScriptConfiguration? existing = IsAssignmentSelected ? CurrentScript : null;
        IsBusy = true;
        ClearStatus();
        try
        {
            ScriptConfiguration configuration =
                await ScriptConfigurationComposer.ValidateAsync(
                    Workspace,
                    target.Scope,
                    scriptSource,
                    existing);
            Guid scriptId = existing?.Id ?? Guid.NewGuid();
            configuration = configuration with
            {
                Id = scriptId,
                Name = string.IsNullOrWhiteSpace(Name) ? GetNewAssignmentName() : Name.Trim(),
            };
            bool applied = Workspace.UpdateDocument(document =>
                ApplyConfiguration(document, target, configuration));
            if (!applied)
            {
                throw new InvalidOperationException(localization["Script.ConfigurationRejected"]);
            }

            RefreshAssignments(scriptId);
            selectedAssignment = null;
            SelectedAssignment = AttachedScripts.First(item => item.Id == scriptId);
            SetStatus(successMessage, error: false);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or HostRequestException or JsonException)
        {
            ApplicationLog.Write("ui.script_validation_failed", exception);
            SetStatus(
                string.Format(
                    localization.Culture,
                    localization["Script.ValidationFailed"],
                    exception.Message),
                error: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void DisableAssignment()
    {
        ScriptTarget target = CurrentTarget;
        Workspace.UpdateDocument(document =>
        {
            ScriptConfiguration? current = FindScript(document, target);
            if (current is null)
            {
                return document;
            }

            return ApplyConfiguration(
                document,
                target,
                current with { Enabled = false },
                activateProfileControl: false);
        });
        RefreshAssignments(SelectedAssignment?.Id);
        LoadAssignment();
        SetStatus(localization["Script.AssignmentDisabled"], error: false);
    }

    private void DeleteAssignment()
    {
        if (SelectedAssignment is null)
        {
            return;
        }

        ScriptTarget target = CurrentTarget;
        Guid scriptId = SelectedAssignment.Id;
        Workspace.UpdateDocument(document => RemoveConfiguration(document, target, scriptId));
        RefreshAssignments();
        SelectDefaultAssignment();
        SetStatus(localization["Script.AssignmentRemoved"], error: false);
    }

    private bool TrySaveLibrary()
    {
        try
        {
            libraryStore.Save(allScripts);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            ApplicationLog.Write("ui.script_library_save_failed", exception);
            SetStatus(localization["Script.LibrarySaveFailed"], error: true);
            return false;
        }
    }

    private void RefreshLibrary(Guid? selectedId = null)
    {
        LibraryScripts.Clear();
        foreach (ScriptLibraryEntry script in allScripts
            .Where(script => script.Scope == Scope)
            .OrderBy(script => script.Name, StringComparer.OrdinalIgnoreCase))
        {
            LibraryScripts.Add(new(script));
        }
        OnPropertyChanged(nameof(HasLibraryScripts));

        if (selectedId is Guid id)
        {
            ScriptLibraryItemViewModel? item = LibraryScripts.FirstOrDefault(script => script.Id == id);
            if (item is not null)
            {
                selectedLibrary = null;
                SelectedLibrary = item;
            }
        }
    }

    private void RefreshAssignments(Guid? selectedId = null)
    {
        AttachedScripts.Clear();
        foreach (ScriptConfiguration script in CurrentScripts)
        {
            AttachedScripts.Add(new(script));
        }
        OnPropertyChanged(nameof(HasAttachedScripts));

        selectedAssignment = selectedId.HasValue
            ? AttachedScripts.FirstOrDefault(script => script.Id == selectedId.Value)
            : null;
        OnPropertyChanged(nameof(SelectedAssignment));
    }

    private void NotifyEditorMode()
    {
        OnPropertyChanged(nameof(IsAssignmentSelected));
        OnPropertyChanged(nameof(IsLibrarySelected));
        OnPropertyChanged(nameof(AssignmentHasScript));
        OnPropertyChanged(nameof(AssignmentEnabled));
    }

    private void SetStatus(string message, bool error)
    {
        StatusText = message;
        HasError = error;
    }

    private void ClearStatus()
    {
        StatusText = null;
        HasError = false;
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(CurrentTargetName));
        if (IsAssignmentSelected)
        {
            OnPropertyChanged(nameof(CurrentTargetName));
        }
    }

    private ScriptConfiguration[] CurrentScripts => Scope switch
    {
        ScriptScope.Profile => Workspace.SelectedProfile.Scripts,
        ScriptScope.ProfileSet => Workspace.SelectedProfileSet?.Scripts ?? [],
        ScriptScope.Global => Workspace.Document.GlobalScripts,
        _ => [],
    };

    private ScriptConfiguration? CurrentScript => SelectedAssignment is null
        ? null
        : CurrentScripts.FirstOrDefault(script => script.Id == SelectedAssignment.Id);

    private ScriptTarget CurrentTarget => new(
        Scope,
        Scope switch
        {
            ScriptScope.Profile => Workspace.SelectedProfileId,
            ScriptScope.ProfileSet => Workspace.SelectedProfileSetId,
            ScriptScope.Global => Guid.Empty,
            _ => Guid.Empty,
        },
        SelectedAssignment?.Id ?? Guid.Empty);

    private static ConfigurationDocument ApplyConfiguration(
        ConfigurationDocument document,
        ScriptTarget target,
        ScriptConfiguration configuration,
        bool activateProfileControl = true) =>
        target.Scope switch
        {
            ScriptScope.Profile => document with
            {
                Profiles = document.Profiles.Select(profile =>
                    profile.Id == target.TargetId
                        ? ApplyProfileConfiguration(profile, configuration, activateProfileControl)
                        : profile).ToArray(),
            },
            ScriptScope.ProfileSet => document with
            {
                ProfileSets = document.ProfileSets.Select(profileSet =>
                    profileSet.Id == target.TargetId
                        ? profileSet with { Scripts = Upsert(profileSet.Scripts, configuration) }
                        : profileSet).ToArray(),
            },
            ScriptScope.Global => document with
            {
                GlobalScripts = Upsert(document.GlobalScripts, configuration),
            },
            _ => document,
        };

    private static Profile ApplyProfileConfiguration(
        Profile profile,
        ScriptConfiguration configuration,
        bool activateProfileControl)
    {
        ScriptConfiguration[] scripts = Upsert(profile.Scripts, configuration);
        return profile with
        {
            Scripts = scripts,
            ControlMode = scripts.Any(script => script.Enabled)
                ? activateProfileControl
                    ? DisplayControlMode.Lua
                    : profile.ControlMode
                : DisplayControlMode.Basic,
        };
    }

    private static ScriptConfiguration? FindScript(
        ConfigurationDocument document,
        ScriptTarget target) =>
        target.Scope switch
        {
            ScriptScope.Profile => document.Profiles
                .FirstOrDefault(profile => profile.Id == target.TargetId)?.Scripts
                .FirstOrDefault(script => script.Id == target.ScriptId),
            ScriptScope.ProfileSet => document.ProfileSets
                .FirstOrDefault(profileSet => profileSet.Id == target.TargetId)?.Scripts
                .FirstOrDefault(script => script.Id == target.ScriptId),
            ScriptScope.Global => document.GlobalScripts
                .FirstOrDefault(script => script.Id == target.ScriptId),
            _ => null,
        };

    private static ConfigurationDocument RemoveConfiguration(
        ConfigurationDocument document,
        ScriptTarget target,
        Guid scriptId) =>
        target.Scope switch
        {
            ScriptScope.Profile => document with
            {
                Profiles = document.Profiles.Select(profile =>
                    profile.Id == target.TargetId
                        ? profile with
                        {
                            Scripts = profile.Scripts.Where(script => script.Id != scriptId).ToArray(),
                            ControlMode = profile.Scripts.Any(script => script.Id != scriptId && script.Enabled)
                                ? profile.ControlMode
                                : DisplayControlMode.Basic,
                        }
                        : profile).ToArray(),
            },
            ScriptScope.ProfileSet => document with
            {
                ProfileSets = document.ProfileSets.Select(profileSet =>
                    profileSet.Id == target.TargetId
                        ? profileSet with
                        {
                            Scripts = profileSet.Scripts.Where(script => script.Id != scriptId).ToArray(),
                        }
                        : profileSet).ToArray(),
            },
            ScriptScope.Global => document with
            {
                GlobalScripts = document.GlobalScripts.Where(script => script.Id != scriptId).ToArray(),
            },
            _ => document,
        };

    private static ScriptConfiguration[] Upsert(
        ScriptConfiguration[] scripts,
        ScriptConfiguration configuration)
    {
        int index = Array.FindIndex(scripts, script => script.Id == configuration.Id);
        if (index < 0)
        {
            return [.. scripts, configuration];
        }

        ScriptConfiguration[] result = scripts.ToArray();
        result[index] = configuration;
        return result;
    }

    private string GetNewAssignmentName()
    {
        string baseName = localization["Script.NewScript"];
        string candidate = baseName;
        int suffix = 2;
        while (CurrentScripts.Any(script =>
            string.Equals(script.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{baseName} {suffix++}";
        }
        return candidate;
    }

    private readonly record struct ScriptTarget(
        ScriptScope Scope,
        Guid TargetId,
        Guid ScriptId = default);
}

internal static class ScriptConfigurationComposer
{
    public static async Task<ScriptConfiguration> ValidateAsync(
        ConfigurationWorkspace workspace,
        ScriptScope scope,
        string source,
        ScriptConfiguration? existing)
    {
        JsonElement payload = JsonSerializer.SerializeToElement(new
        {
            scope = ScopeName(scope),
            source,
            settings = (existing?.Settings ?? []).Select(setting => new
            {
                id = setting.Id,
                type = SettingTypeName(setting.Type),
                value = setting.Value,
            }),
        });
        JsonElement response = await workspace.ValidateScriptAsync(payload);
        return MergeDeclarations(
            source,
            existing,
            response.GetProperty("declarations"));
    }

    private static ScriptConfiguration MergeDeclarations(
        string source,
        ScriptConfiguration? existing,
        JsonElement declarations)
    {
        Dictionary<string, ScriptBindingSlot> oldBindings = existing?.Bindings
            .ToDictionary(binding => binding.Id, StringComparer.Ordinal) ?? [];
        Dictionary<string, ScriptSetting> oldSettings = existing?.Settings
            .ToDictionary(setting => setting.Id, StringComparer.Ordinal) ?? [];

        List<ScriptBindingSlot> bindings = [];
        foreach (JsonElement declaration in declarations.GetProperty("bindings").EnumerateArray())
        {
            string id = declaration.GetProperty("id").GetString()!;
            bool defaultEnabled = declaration.GetProperty("defaultEnabled").GetBoolean();
            KeyIdentity? defaultKey = ParseDefaultKey(declaration);
            if (!oldBindings.TryGetValue(id, out ScriptBindingSlot? old))
            {
                old = new ScriptBindingSlot(
                    id,
                    declaration.GetProperty("displayName").GetString()!,
                    declaration.GetProperty("pressed").GetBoolean(),
                    declaration.GetProperty("released").GetBoolean(),
                    defaultEnabled,
                    defaultKey);
            }
            bindings.Add(old with
            {
                DisplayName = declaration.GetProperty("displayName").GetString()!,
                Pressed = declaration.GetProperty("pressed").GetBoolean(),
                Released = declaration.GetProperty("released").GetBoolean(),
            });
        }

        List<ScriptSetting> settings = [];
        foreach (JsonElement declaration in declarations.GetProperty("settings").EnumerateArray())
        {
            string id = declaration.GetProperty("id").GetString()!;
            ScriptSettingType type = ParseSettingType(declaration.GetProperty("type").GetString()!);
            string[] options = declaration.GetProperty("options")
                .EnumerateArray()
                .Select(option => option.GetString()!)
                .ToArray();
            double? minimum = ReadNullableDouble(declaration.GetProperty("minimum"));
            double? maximum = ReadNullableDouble(declaration.GetProperty("maximum"));
            string defaultValue = declaration.GetProperty("defaultValue").GetString()!;
            if (!oldSettings.TryGetValue(id, out ScriptSetting? old) || old.Type != type)
            {
                old = new ScriptSetting(
                    id,
                    declaration.GetProperty("displayName").GetString()!,
                    type,
                    defaultValue,
                    options,
                    minimum,
                    maximum);
            }
            settings.Add(old with
            {
                DisplayName = declaration.GetProperty("displayName").GetString()!,
                Type = type,
                Options = options,
                Minimum = minimum,
                Maximum = maximum,
            });
        }

        return new ScriptConfiguration(
            true,
            declarations.GetProperty("apiVersion").GetString()!,
            source,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))),
            bindings.ToArray(),
            settings.ToArray(),
            ParseUi(declarations.GetProperty("ui")));
    }

    private static ScriptUiLayout? ParseUi(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return new ScriptUiLayout(
            value.GetProperty("sections")
                .EnumerateArray()
                .Select(section => new ScriptUiSection(
                    section.GetProperty("id").GetString()!,
                    section.GetProperty("displayName").GetString()!,
                    section.GetProperty("description").GetString()!,
                    section.GetProperty("collapsible").GetBoolean(),
                    section.GetProperty("defaultExpanded").GetBoolean(),
                    section.GetProperty("columns").GetInt32(),
                    section.GetProperty("items")
                        .EnumerateArray()
                        .Select(item => new ScriptUiItem(
                            item.GetProperty("settingId").GetString()!,
                            ParseUiControl(item.GetProperty("control").GetString()!),
                            item.GetProperty("description").GetString()!,
                            item.GetProperty("unit").GetString()!,
                            ReadNullableDouble(item.GetProperty("step")),
                            ParseVisibilityCondition(item.GetProperty("visibleWhen"))))
                        .ToArray()))
                .ToArray());
    }

    private static ScriptUiVisibilityCondition? ParseVisibilityCondition(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null
            ? null
            : new ScriptUiVisibilityCondition(
                value.GetProperty("settingId").GetString()!,
                value.GetProperty("equalsValue").GetString()!);

    private static KeyIdentity? ParseDefaultKey(JsonElement declaration)
    {
        if (!declaration.TryGetProperty("defaultKey", out JsonElement value) ||
            value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        InputDeviceKind device = value.GetProperty("device").GetString() switch
        {
            "keyboard" => InputDeviceKind.Keyboard,
            "mouse" => InputDeviceKind.Mouse,
            _ => throw new JsonException("Script binding default device is invalid."),
        };
        return new KeyIdentity(
            device,
            value.GetProperty("code").GetUInt16(),
            value.GetProperty("extended").GetBoolean(),
            ParseKeyModifiers(value.GetProperty("modifiers").GetString()!));
    }

    private static KeyModifiers ParseKeyModifiers(string value)
    {
        if (value is "" or "none")
        {
            return KeyModifiers.None;
        }

        KeyModifiers result = KeyModifiers.None;
        foreach (string token in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            KeyModifiers modifier = token switch
            {
                "ctrl" => KeyModifiers.Ctrl,
                "alt" => KeyModifiers.Alt,
                "shift" => KeyModifiers.Shift,
                "win" => KeyModifiers.Win,
                _ => throw new JsonException("Script binding default modifiers are invalid."),
            };
            if ((result & modifier) != 0)
            {
                throw new JsonException("Script binding default modifiers contain a duplicate value.");
            }
            result |= modifier;
        }
        return result;
    }

    private static ScriptUiControlType ParseUiControl(string value) => value switch
    {
        "auto" => ScriptUiControlType.Auto,
        "switch" => ScriptUiControlType.Switch,
        "checkbox" => ScriptUiControlType.Checkbox,
        "slider" => ScriptUiControlType.Slider,
        "number" => ScriptUiControlType.Number,
        "textbox" => ScriptUiControlType.Textbox,
        "select" => ScriptUiControlType.Select,
        "segmented" => ScriptUiControlType.Segmented,
        _ => throw new JsonException("Script UI control type is invalid."),
    };

    private static string ScopeName(ScriptScope scope) => scope switch
    {
        ScriptScope.Profile => "profile",
        ScriptScope.ProfileSet => "profileSet",
        ScriptScope.Global => "global",
        _ => throw new ArgumentOutOfRangeException(nameof(scope)),
    };

    private static string SettingTypeName(ScriptSettingType type) => type switch
    {
        ScriptSettingType.Boolean => "boolean",
        ScriptSettingType.Integer => "integer",
        ScriptSettingType.Double => "double",
        ScriptSettingType.String => "string",
        ScriptSettingType.Enum => "enum",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private static ScriptSettingType ParseSettingType(string value) => value switch
    {
        "boolean" => ScriptSettingType.Boolean,
        "integer" => ScriptSettingType.Integer,
        "double" => ScriptSettingType.Double,
        "string" => ScriptSettingType.String,
        "enum" => ScriptSettingType.Enum,
        _ => throw new JsonException("Script setting type is invalid."),
    };

    private static double? ReadNullableDouble(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
}
