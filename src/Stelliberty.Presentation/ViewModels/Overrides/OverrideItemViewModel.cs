using Stelliberty.Application.Localization;
using Stelliberty.Application.Overrides;
using Stelliberty.Domain.Overrides;
using Stelliberty.Presentation.Formatting;

namespace Stelliberty.Presentation.ViewModels;

public sealed class OverrideItemViewModel : ViewModelBase
{
    private string _name;
    private string _sourceLocation;
    private OverrideFormat _format;
    private OverrideUpdateProxyMode _updateProxyMode;
    private bool _isUpdating;
    private readonly Func<DateTimeOffset?>? _getLocalLastModifiedAt;
    private DateTimeOffset? _localLastModifiedAt;

    public OverrideItemViewModel(
        string id,
        string name,
        string sourceLocation,
        OverrideFormat format,
        bool isLocalFile,
        OverrideUpdateProxyMode updateProxyMode = OverrideUpdateProxyMode.Direct,
        bool isCreatedBlank = false,
        DateTimeOffset? lastUpdatedAt = null,
        ILocalizationService? localization = null,
        Func<DateTimeOffset?>? getLocalLastModifiedAt = null)
    {
        Id = id;
        _name = name;
        _sourceLocation = sourceLocation;
        _format = format;
        IsLocalFile = isLocalFile;
        _getLocalLastModifiedAt = getLocalLastModifiedAt;
        _localLastModifiedAt = getLocalLastModifiedAt?.Invoke();
        _updateProxyMode = updateProxyMode;
        IsCreatedBlank = isCreatedBlank;
        LastUpdatedAt = lastUpdatedAt;
        Localization = localization;
    }

    public string Id { get; }

    public string Name
    {
        get => _name;
        private set => SetProperty(ref _name, value);
    }

    public string SourceLocation
    {
        get => _sourceLocation;
        private set => SetProperty(ref _sourceLocation, value);
    }

    public OverrideFormat Format
    {
        get => _format;
        private set
        {
            if (SetProperty(ref _format, value))
            {
                OnPropertyChanged(nameof(FormatText));
            }
        }
    }

    public bool IsLocalFile { get; }

    public OverrideUpdateProxyMode UpdateProxyMode
    {
        get => _updateProxyMode;
        private set => SetProperty(ref _updateProxyMode, value);
    }

    public bool IsCreatedBlank { get; }

    public DateTimeOffset? LastUpdatedAt { get; }

    public ILocalizationService? Localization { get; }

    public string FormatText => Format == OverrideFormat.Yaml
        ? Localize("Overrides.Format.Yaml")
        : Localize("Overrides.Format.JavaScript");

    public string SourceText => IsLocalFile ? Localize("Overrides.Source.Local") : Localize("Overrides.Source.Remote");

    public string SourcePillTag => IsLocalFile ? "local" : "remote";

    private DateTimeOffset? DisplayedLastUpdatedAt => IsLocalFile ? _localLastModifiedAt : LastUpdatedAt;

    public string LastUpdatedIconType => IsLocalFile ? "FileLine" : "Download3Line";

    public string LastUpdatedText => RelativeTime.Format(DisplayedLastUpdatedAt, Localization);

    public string LastUpdatedTooltip => DisplayedLastUpdatedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? Localize("Common.NotUpdated");

    public void RefreshRelativeTime()
    {
        if (IsLocalFile)
        {
            _localLastModifiedAt = _getLocalLastModifiedAt?.Invoke();
        }

        OnPropertyChanged(nameof(LastUpdatedText));
        OnPropertyChanged(nameof(LastUpdatedTooltip));
    }

    public string RowAutomationId => $"Overrides.Row.{Id}";

    public string UpdateAutomationId => $"Overrides.Row.{Id}.UpdateButton";

    public string MenuAutomationId => $"Overrides.Row.{Id}.MenuButton";

    public bool IsUpdateVisible => !IsLocalFile;

    public bool IsUpdating
    {
        get => _isUpdating;
        private set
        {
            if (SetProperty(ref _isUpdating, value))
            {
                OnPropertyChanged(nameof(IsUpdateIconVisible));
                OnPropertyChanged(nameof(IsUpdateButtonEnabled));
            }
        }
    }

    public bool IsUpdateIconVisible => !IsUpdating;

    public bool IsUpdateButtonEnabled => !IsUpdating;

    public IReadOnlyList<OverrideRowMenuSelection> MenuOptions =>
    [
        new(Id, OverrideRowMenuAction.Edit, Localize("Overrides.Menu.Edit")),
        new(Id, OverrideRowMenuAction.EditFile, Localize("Overrides.Menu.EditFile")),
        new(Id, OverrideRowMenuAction.OpenExternalEditor, Localize("Overrides.Menu.ExternalEditor")),
        new(Id, OverrideRowMenuAction.Delete, Localize("Common.Delete"))
    ];

    public void UpdateConfiguration(
        string name,
        string sourceLocation,
        OverrideFormat format,
        OverrideUpdateProxyMode updateProxyMode)
    {
        Name = name;
        SourceLocation = sourceLocation;
        Format = format;
        UpdateProxyMode = updateProxyMode;
    }

    public void SetUpdating(bool isUpdating)
    {
        IsUpdating = isUpdating;
    }

    public void RefreshLanguage()
    {
        OnPropertyChanged(nameof(FormatText));
        OnPropertyChanged(nameof(SourceText));
        OnPropertyChanged(nameof(LastUpdatedText));
        OnPropertyChanged(nameof(LastUpdatedTooltip));
        OnPropertyChanged(nameof(MenuOptions));
    }

    private string Localize(string key) => Localization?.GetString(key) ?? key;
}
