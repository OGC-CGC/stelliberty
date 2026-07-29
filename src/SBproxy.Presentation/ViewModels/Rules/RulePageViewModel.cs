using System.Windows.Input;
using SBproxy.Application.Localization;
using SBproxy.Application.Rules;
using SBproxy.Domain.Rules;
using SBproxy.Presentation.Collections;
using SBproxy.Presentation.Commands;

namespace SBproxy.Presentation.ViewModels;

public sealed class RulePageViewModel : ViewModelBase, IDisposable
{
    private readonly RuleSearch _search = new();
    private readonly RuleListLoader? _loader;
    private readonly ILocalizationService? _localization;
    private IReadOnlyList<RuleItem> _rules = [];
    private IReadOnlyList<RuleItem> _filteredRules = [];
    private readonly LazyRowList<RuleItem, RuleRowViewModel> _filteredRuleRows;
    private string _searchKeyword = string.Empty;
    private RuleTypeBucket _typeBucket = RuleTypeBucket.All;
    private bool _isCoreRunning = true;
    private bool _hasRequestedRefresh;

    public RulePageViewModel(RuleListLoader? loader = null, ILocalizationService? localization = null)
    {
        _loader = loader;
        _localization = localization;
        _filteredRuleRows = new LazyRowList<RuleItem, RuleRowViewModel>(
            (rule, index) => new RuleRowViewModel(index + 1, rule, _localization));
        if (_localization is not null)
        {
            _localization.LanguageChanged += OnLanguageChanged;
        }
        RefreshRulesCommand = new RelayCommand(RequestRefresh);
        ShowAllTypesCommand = new RelayCommand(() => SetTypeBucket(RuleTypeBucket.All));
        ShowDomainRulesCommand = new RelayCommand(() => SetTypeBucket(RuleTypeBucket.Domain));
        ShowIpRulesCommand = new RelayCommand(() => SetTypeBucket(RuleTypeBucket.Ip));
        ShowRuleSetRulesCommand = new RelayCommand(() => SetTypeBucket(RuleTypeBucket.RuleSet));
        ShowOtherRulesCommand = new RelayCommand(() => SetTypeBucket(RuleTypeBucket.Other));
    }

    public event EventHandler? RefreshRequested;

    public IReadOnlyList<RuleItem> Rules => _rules;

    public IReadOnlyList<RuleItem> FilteredRules => _filteredRules;

    public IReadOnlyList<RuleRowViewModel> FilteredRuleRows => _filteredRuleRows;

    public bool IsCoreRunning => _isCoreRunning;

    public string MonitorStateText => _isCoreRunning ? Localize("Rules.State.Monitoring") : Localize("Rules.State.CoreStopped");

    public string MonitorSignalTag => _isCoreRunning ? "ok" : "warning";

    public bool HasRequestedRefresh => _hasRequestedRefresh;

    public bool IsEmptyVisible => !_isCoreRunning || _filteredRules.Count == 0;

    public string EmptyText => !_isCoreRunning
        ? Localize("Rules.Empty.CoreStopped")
        : _rules.Count == 0
            ? Localize("Rules.Empty.NoRules")
            : Localize("Rules.Empty.NoMatches");

    public RuleTypeBucket TypeBucket => _typeBucket;

    public bool IsAllTypesSelected => _typeBucket == RuleTypeBucket.All;
    public bool IsDomainRulesSelected => _typeBucket == RuleTypeBucket.Domain;
    public bool IsIpRulesSelected => _typeBucket == RuleTypeBucket.Ip;
    public bool IsRuleSetRulesSelected => _typeBucket == RuleTypeBucket.RuleSet;
    public bool IsOtherRulesSelected => _typeBucket == RuleTypeBucket.Other;

    public string SearchKeyword
    {
        get => _searchKeyword;
        set
        {
            if (string.Equals(_searchKeyword, value, StringComparison.Ordinal))
            {
                return;
            }

            _searchKeyword = value;
            RaiseRuleStateChanged();
        }
    }

    public ICommand RefreshRulesCommand { get; }
    public ICommand ShowAllTypesCommand { get; }
    public ICommand ShowDomainRulesCommand { get; }
    public ICommand ShowIpRulesCommand { get; }
    public ICommand ShowRuleSetRulesCommand { get; }
    public ICommand ShowOtherRulesCommand { get; }

    public void LoadRules(IReadOnlyList<RuleItem> rules)
    {
        _rules = rules;
        RaiseRuleStateChanged();
    }

    public void SetTypeBucket(RuleTypeBucket bucket)
    {
        if (_typeBucket == bucket)
        {
            return;
        }

        _typeBucket = bucket;
        RaiseRuleStateChanged();
    }

    private void RequestRefresh()
    {
        _hasRequestedRefresh = true;
        if (_loader is not null)
        {
            LoadRules(_loader.LoadRules());
        }

        OnPropertyChanged(nameof(HasRequestedRefresh));
        RefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    public void ApplyCoreRunning(bool isRunning)
    {
        if (_isCoreRunning == isRunning)
        {
            return;
        }

        _isCoreRunning = isRunning;
        if (!isRunning)
        {
            // 停止只重置筛选；数据保留，便于同配置重启后恢复。
            _searchKeyword = string.Empty;
            _typeBucket = RuleTypeBucket.All;
        }

        RaiseRuleStateChanged();
    }

    private static bool MatchesBucket(string type, RuleTypeBucket bucket)
    {
        return RuleTypeClassifier.MatchesBucket(type, bucket);
    }

    private void RaiseRuleStateChanged()
    {
        RebuildFilteredRows();
        OnPropertyChanged(nameof(Rules));
        OnPropertyChanged(nameof(FilteredRules));
        OnPropertyChanged(nameof(FilteredRuleRows));
        OnPropertyChanged(nameof(IsCoreRunning));
        OnPropertyChanged(nameof(MonitorStateText));
        OnPropertyChanged(nameof(MonitorSignalTag));
        OnPropertyChanged(nameof(HasRequestedRefresh));
        OnPropertyChanged(nameof(IsEmptyVisible));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(SearchKeyword));
        OnPropertyChanged(nameof(TypeBucket));
        OnPropertyChanged(nameof(IsAllTypesSelected));
        OnPropertyChanged(nameof(IsDomainRulesSelected));
        OnPropertyChanged(nameof(IsIpRulesSelected));
        OnPropertyChanged(nameof(IsRuleSetRulesSelected));
        OnPropertyChanged(nameof(IsOtherRulesSelected));
    }

    // 筛选结果保留轻量数据快照，行模型由虚拟化面板按视口创建。
    private void RebuildFilteredRows()
    {
        _filteredRules = _search
            .Filter(_rules, _searchKeyword)
            .Where(rule => MatchesBucket(rule.Type, _typeBucket))
            .ToList();
        _filteredRuleRows.Replace(_filteredRules);
    }

    public void ReleasePresentationRows() => _filteredRuleRows.RecycleAll();

    public void Dispose()
    {
        _filteredRuleRows.Release();
        if (_localization is not null)
        {
            _localization.LanguageChanged -= OnLanguageChanged;
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs args)
    {
        RaiseRuleStateChanged();
    }

    private string Localize(string key) => _localization?.GetString(key) ?? key;
}
