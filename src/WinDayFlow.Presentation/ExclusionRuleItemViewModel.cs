using CommunityToolkit.Mvvm.ComponentModel;
using WinDayFlow.Application.Settings;

namespace WinDayFlow.Presentation.Settings;

public sealed class ExclusionRuleItemViewModel : ObservableObject
{
    private CaptureExclusionRule _rule;
    private int _index;
    private int _ruleCount;

    internal ExclusionRuleItemViewModel(
        CaptureExclusionRule rule,
        int index,
        int ruleCount)
    {
        _rule = rule ?? throw new ArgumentNullException(nameof(rule));
        _index = ValidateIndex(index, ruleCount);
        _ruleCount = ruleCount;
    }

    public Guid Id => _rule.Id;

    public string Name => _rule.Name;

    public bool IsEnabled => _rule.Enabled;

    public CaptureExclusionRuleScope Scope => _rule.Scope;

    public ApplicationIdentityKind ApplicationIdentityKind =>
        _rule.ApplicationIdentityKind;

    public string IdentityValue => _rule.IdentityValue;

    public WindowTitleMatchKind? WindowTitleMatchKind => _rule.WindowTitleMatchKind;

    public string? Pattern => _rule.Pattern;

    public long Revision => _rule.Revision;

    public int Index => _index;

    public bool CanMoveUp => _index > 0;

    public bool CanMoveDown => _index < _ruleCount - 1;

    public string StatusText => IsEnabled ? "规则已启用" : "规则已停用";

    public string ConfiguredMatchSummaryText
    {
        get
        {
            var identity = ApplicationIdentityKind switch
            {
                ApplicationIdentityKind.ExecutableName => "EXE 文件名",
                ApplicationIdentityKind.PackageFamilyName => "包系列名称",
                ApplicationIdentityKind.PublisherCertificateSha256 => "发布者证书 SHA-256",
                _ => "应用身份",
            };
            if (Scope == CaptureExclusionRuleScope.Application)
            {
                return $"整个应用 · {identity}：{IdentityValue}";
            }

            var match = WindowTitleMatchKind switch
            {
                WinDayFlow.Application.Settings.WindowTitleMatchKind.Exact => "完全匹配",
                WinDayFlow.Application.Settings.WindowTitleMatchKind.StartsWith => "开头匹配",
                WinDayFlow.Application.Settings.WindowTitleMatchKind.Contains => "包含",
                _ => "匹配",
            };
            return $"特定窗口 · {identity}：{IdentityValue} · 标题{match}：{Pattern}";
        }
    }

    public string ToggleAutomationName => $"{(IsEnabled ? "停用" : "启用")}规则 {Name}";

    public string MoveUpAutomationName => $"上移规则 {Name}";

    public string MoveDownAutomationName => $"下移规则 {Name}";

    public string EditAutomationName => $"编辑规则 {Name}";

    public string DeleteAutomationName => $"删除规则 {Name}";

    public string ToggleAutomationId => $"ExclusionRuleToggle_{Id:D}";

    public string MoveUpAutomationId => $"ExclusionRuleMoveUp_{Id:D}";

    public string MoveDownAutomationId => $"ExclusionRuleMoveDown_{Id:D}";

    public string EditAutomationId => $"ExclusionRuleEdit_{Id:D}";

    public string DeleteAutomationId => $"ExclusionRuleDelete_{Id:D}";

    internal void Update(CaptureExclusionRule rule, int index, int ruleCount)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (rule.Id != Id)
        {
            throw new ArgumentException(
                "An exclusion-rule item cannot change its identifier.",
                nameof(rule));
        }

        _ = ValidateIndex(index, ruleCount);
        var ruleChanged = _rule != rule;
        var positionChanged = _index != index || _ruleCount != ruleCount;
        _rule = rule;
        _index = index;
        _ruleCount = ruleCount;

        if (ruleChanged)
        {
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(IsEnabled));
            OnPropertyChanged(nameof(Scope));
            OnPropertyChanged(nameof(ApplicationIdentityKind));
            OnPropertyChanged(nameof(IdentityValue));
            OnPropertyChanged(nameof(WindowTitleMatchKind));
            OnPropertyChanged(nameof(Pattern));
            OnPropertyChanged(nameof(Revision));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(ConfiguredMatchSummaryText));
            OnPropertyChanged(nameof(ToggleAutomationName));
            OnPropertyChanged(nameof(MoveUpAutomationName));
            OnPropertyChanged(nameof(MoveDownAutomationName));
            OnPropertyChanged(nameof(EditAutomationName));
            OnPropertyChanged(nameof(DeleteAutomationName));
        }

        if (positionChanged)
        {
            OnPropertyChanged(nameof(Index));
            OnPropertyChanged(nameof(CanMoveUp));
            OnPropertyChanged(nameof(CanMoveDown));
        }
    }

    private static int ValidateIndex(int index, int ruleCount)
    {
        if (ruleCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ruleCount),
                ruleCount,
                "An exclusion-rule item requires a non-empty collection.");
        }

        if (index < 0 || index >= ruleCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index),
                index,
                "The exclusion-rule item index must be within the collection.");
        }

        return index;
    }
}
