using SBproxy.Application.Diagnostics;
using SBproxy.Application.Localization;
using SBproxy.Application.Settings;

namespace SBproxy.Presentation.ViewModels;

public sealed class SettingsLanguageViewModel : ViewModelBase, IDisposable
{
    private readonly AppSettings _settings;
    private readonly IAppSettingsStore _settingsStore;
    private readonly ILocalizationService _localization;
    private IReadOnlyList<SelectionOption<AppLanguage>> _options;
    private AppLanguage _optionsLanguage;
    private AppLanguage _selectedLanguage;

    public SettingsLanguageViewModel(AppSettings settings, IAppSettingsStore settingsStore, ILocalizationService localization)
    {
        _settings = settings;
        _settingsStore = settingsStore;
        _localization = localization;
        _selectedLanguage = ParseEnum(settings.Language, AppLanguage.System);
        _localization.SetLanguage(_selectedLanguage);
        _optionsLanguage = _localization.EffectiveLanguage;
        _options = CreateOptions();
        _localization.LanguageChanged += OnLanguageChanged;
    }

    public string FieldLabelText => _localization.GetString("Settings.Field.Language");

    public IReadOnlyList<SelectionOption<AppLanguage>> Options => _options;

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
        var restoredLanguage = ParseEnum(_settings.Language, AppLanguage.System);
        if (restoredLanguage == _selectedLanguage)
        {
            OnLanguageChanged(this, EventArgs.Empty);
            return;
        }

        _selectedLanguage = restoredLanguage;
        _localization.SetLanguage(restoredLanguage);
    }

    public void Dispose()
    {
        _localization.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(FieldLabelText));
        var effectiveLanguage = _localization.EffectiveLanguage;
        if (_optionsLanguage != effectiveLanguage)
        {
            _optionsLanguage = effectiveLanguage;
            _options = CreateOptions();
            OnPropertyChanged(nameof(Options));
        }

        OnPropertyChanged(nameof(SelectedOption));
    }

    private IReadOnlyList<SelectionOption<AppLanguage>> CreateOptions()
    {
        return
        [
            new(AppLanguage.System, _localization.GetString("Language.Option.System")),
            new(AppLanguage.ZhHans, _localization.GetString("Language.Option.ZhHans")),
            new(AppLanguage.ZhHant, _localization.GetString("Language.Option.ZhHant")),
            new(AppLanguage.En, _localization.GetString("Language.Option.En")),
        ];
    }

    private static T ParseEnum<T>(string value, T fallback) where T : struct
    {
        return Enum.TryParse<T>(value, out var parsed) ? parsed : fallback;
    }
}
