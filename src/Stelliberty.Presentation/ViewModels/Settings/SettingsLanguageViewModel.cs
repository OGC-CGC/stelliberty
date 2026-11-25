using Stelliberty.Application.Diagnostics;
using Stelliberty.Application.Localization;
using Stelliberty.Application.Settings;

namespace Stelliberty.Presentation.ViewModels;

public sealed class SettingsLanguageViewModel : ViewModelBase, IDisposable
{
    private readonly AppSettings _settings;
    private readonly IAppSettingsStore _settingsStore;
    private readonly ILocalizationService _localization;
    private AppLanguage _selectedLanguage;
    private bool _displayTrafficInBytes;

    public SettingsLanguageViewModel(AppSettings settings, IAppSettingsStore settingsStore, ILocalizationService localization)
    {
        _settings = settings;
        _settingsStore = settingsStore;
        _localization = localization;
        _selectedLanguage = ParseEnum(settings.Language, AppLanguage.System);
        _displayTrafficInBytes = settings.DisplayTrafficInBytes;
        _localization.SetLanguage(_selectedLanguage);
        _localization.LanguageChanged += OnLanguageChanged;
    }

    public string FieldLabelText => _localization.GetString("Settings.Field.Language");

    public string TrafficUnitLabelText => _localization.GetString("Settings.Field.TrafficUnit");

    public event Action<bool>? TrafficUnitChanged;

    public IReadOnlyList<SelectionOption<bool>> TrafficUnitOptions { get; } =
        [new(false, "bit"), new(true, "Byte")];

    public SelectionOption<bool>? SelectedTrafficUnit
    {
        get => TrafficUnitOptions[_displayTrafficInBytes ? 1 : 0];
        set
        {
            if (value is null || value.Value == _displayTrafficInBytes)
            {
                return;
            }

            var previous = _settings.DisplayTrafficInBytes;
            _settings.DisplayTrafficInBytes = value.Value;
            try
            {
                _settingsStore.Save(_settings);
            }
            catch
            {
                _settings.DisplayTrafficInBytes = previous;
                throw;
            }

            ApplyTrafficUnit(_settings.DisplayTrafficInBytes);
        }
    }

    public IReadOnlyList<SelectionOption<AppLanguage>> Options =>
    [
        new(AppLanguage.System, _localization.GetString("Language.Option.System")),
        new(AppLanguage.ZhHans, _localization.GetString("Language.Option.ZhHans")),
        new(AppLanguage.ZhHant, _localization.GetString("Language.Option.ZhHant")),
        new(AppLanguage.En, _localization.GetString("Language.Option.En")),
    ];

    public SelectionOption<AppLanguage> SelectedOption
    {
        get => Options.First(option => option.Value == _selectedLanguage);
        set
        {
            if (value.Value == _selectedLanguage)
            {
                return;
            }

            _selectedLanguage = value.Value;
            _settings.Language = value.Value.ToString();
            _settingsStore.Save(_settings);
            _localization.SetLanguage(value.Value);
            AppLogger.Info($"Language switched to {value.DisplayName}");
        }
    }

    public void SetLanguage(AppLanguage language)
    {
        SelectedOption = Options.First(option => option.Value == language);
    }

    public void RefreshFromSettings()
    {
        ApplyTrafficUnit(_settings.DisplayTrafficInBytes);
        var restoredLanguage = ParseEnum(_settings.Language, AppLanguage.System);
        if (restoredLanguage == _selectedLanguage)
        {
            OnLanguageChanged(this, EventArgs.Empty);
            return;
        }

        _selectedLanguage = restoredLanguage;
        _localization.SetLanguage(restoredLanguage);
    }

    private void ApplyTrafficUnit(bool displayInBytes)
    {
        if (SetProperty(ref _displayTrafficInBytes, displayInBytes, nameof(SelectedTrafficUnit)))
        {
            TrafficUnitChanged?.Invoke(displayInBytes);
        }
    }

    public void Dispose()
    {
        _localization.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(FieldLabelText));
        OnPropertyChanged(nameof(TrafficUnitLabelText));
        OnPropertyChanged(nameof(Options));
        OnPropertyChanged(nameof(SelectedOption));
    }

    private static T ParseEnum<T>(string value, T fallback) where T : struct
    {
        return Enum.TryParse<T>(value, out var parsed) ? parsed : fallback;
    }
}
