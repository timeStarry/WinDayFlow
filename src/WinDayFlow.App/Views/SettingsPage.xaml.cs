using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinDayFlow.Application.Ai;
using WinDayFlow.Application.Settings;
using WinDayFlow.Presentation.Settings;

namespace WinDayFlow.App.Views;

public sealed partial class SettingsPage : Page
{
    private const double StackedLayoutMaximumWidth = 720;

    private bool _dialogOpen;
    private ExclusionRuleItemViewModel? _editingExclusionRule;
    private AiProviderProfileItemViewModel? _editingProvider;
    private bool _isSubscribed;
    private bool _isUpdatingCaptureInterval;
    private bool _isUpdatingCaptureToggle;
    private bool _isUpdatingExclusionRuleControls;
    private bool _isUpdatingPrivacyControls;
    private bool _isUpdatingThemePicker;
    private bool _useStackedExclusionRuleLayout;

    public SettingsPage()
    {
        ViewModel = App.GetService<SettingsViewModel>();
        RoutingViewModel = App.GetService<AiRoutingSettingsViewModel>();
        InitializeComponent();
        DataFolderTextBox.Text = App.DataDirectoryPath;
        AppVersionTextBlock.Text = $"版本 {GetApplicationDisplayVersion()}";
        SettingsNavigation.SelectedItem = RecordingSettingsNavigationItem;
        ShowSettingsSection("capture");
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        SynchronizeThemePicker();
        SynchronizeCaptureToggle();
        SynchronizeCaptureInterval();
        SynchronizePrivacyControls();
        UpdateConsentActions();
        UpdateCaptureInformation();
        UpdateExclusionRuleInformation();
    }

    public SettingsViewModel ViewModel { get; }

    public AiRoutingSettingsViewModel RoutingViewModel { get; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_isSubscribed)
        {
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            RoutingViewModel.PropertyChanged += OnRoutingViewModelPropertyChanged;
            _isSubscribed = true;
        }

        await RoutingViewModel.LoadAsync();

        UpdateResponsiveLayout(ActualWidth);
        SynchronizeThemePicker();
        SynchronizeCaptureToggle();
        SynchronizeCaptureInterval();
        SynchronizePrivacyControls();
        UpdateConsentActions();
        UpdateCaptureInformation();
        UpdateExclusionRuleInformation();
        UpdateErrorInformation();
        UpdateRoutingInformation();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_isSubscribed)
        {
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            RoutingViewModel.PropertyChanged -= OnRoutingViewModelPropertyChanged;
            _isSubscribed = false;
        }

        SizeChanged -= OnSizeChanged;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        ViewModel.Dispose();
        RoutingViewModel.Dispose();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateResponsiveLayout(e.NewSize.Width);
    }

    private void OnSettingsSectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is string section)
        {
            ShowSettingsSection(section);
        }
    }

    private void ShowSettingsSection(string section)
    {
        CaptureSection.Visibility = section == "capture"
            ? Visibility.Visible
            : Visibility.Collapsed;
        StorageSection.Visibility = section == "storage"
            ? Visibility.Visible
            : Visibility.Collapsed;
        PrivacySection.Visibility = section == "privacy"
            ? Visibility.Visible
            : Visibility.Collapsed;
        ProviderSection.Visibility = section == "providers"
            ? Visibility.Visible
            : Visibility.Collapsed;
        AppearanceSection.Visibility = section == "appearance"
            ? Visibility.Visible
            : Visibility.Collapsed;
        AboutSection.Visibility = section == "about"
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void OnOpenDataFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(App.DataDirectoryPath);
            if (!await Windows.System.Launcher.LaunchFolderPathAsync(App.DataDirectoryPath))
            {
                ShowPageError("Windows 无法打开本地数据目录。");
            }
        }
        catch (Exception)
        {
            ShowPageError("本地数据目录暂时无法打开。");
        }
    }

    private async void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingThemePicker
            || ThemePicker.SelectedItem is not ComboBoxItem { Tag: string value }
            || !Enum.TryParse<AppThemePreference>(value, out var theme)
            || theme == ViewModel.Theme)
        {
            return;
        }

        if (await ViewModel.SetThemeAsync(theme))
        {
            App.Current.ApplyTheme(theme);
        }
        else
        {
            SynchronizeThemePicker();
        }

        UpdateErrorInformation();
    }

    private async void OnGrantConsent(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen || !ViewModel.CanGrantConsent)
        {
            return;
        }

        var content = new StackPanel
        {
            MaxWidth = 500,
            Spacing = 10,
        };
        content.Children.Add(CreateDialogText(
            "启用录制后，WinDayFlow 可定期采集屏幕图像与前台应用上下文，用于生成时间线和工作回顾。"));
        content.Children.Add(CreateDialogText(
            "保存位置：录制证据默认保存在本机的 WinDayFlow 数据目录。"));
        content.Children.Add(CreateDialogText(
            "外部传输：录制证据默认不离开本机。只有单独配置、测试并启用云端分析后，才会向明确显示的提供方发送提取的静态截图和筛选后的上下文；不会发送完整视频。"));
        content.Children.Add(CreateDialogText(
            "控制权：你可以随时停止录制或撤回同意。撤回不会自动删除已有本地数据。"));
        content.Children.Add(CreateDialogText(
            $"保留策略：{ViewModel.RetentionSummaryText}。"));
        content.Children.Add(CreateDialogText(
            $"隐私选择：{ViewModel.PrivacySummaryText}。"));
        content.Children.Add(CreateDialogText(
            $"录制说明版本：{AppSettingsService.CurrentRecordingConsentVersion}"));

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "允许屏幕活动录制？",
            Content = content,
            PrimaryButtonText = "同意并继续",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        var result = await ShowDialogAsync(dialog);
        if (result == ContentDialogResult.Primary)
        {
            await ViewModel.GrantRecordingConsentAsync();
            UpdateErrorInformation();
        }
    }

    private async void OnRevokeConsent(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen || !ViewModel.CanRevokeConsent)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "撤回录制同意？",
            Content = CreateDialogText(
                "录制会先安全停止并保持关闭。已有本地数据不会自动删除。"),
            PrimaryButtonText = "撤回同意",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        var result = await ShowDialogAsync(dialog);
        if (result == ContentDialogResult.Primary)
        {
            await ViewModel.RevokeRecordingConsentAsync();
            UpdateErrorInformation();
        }
    }

    private async void OnCaptureToggled(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingCaptureToggle
            || CaptureToggle.IsOn == ViewModel.CaptureEnabled)
        {
            return;
        }

        if (!await ViewModel.SetCaptureEnabledAsync(CaptureToggle.IsOn))
        {
            SynchronizeCaptureToggle();
        }

        UpdateErrorInformation();
    }

    private async void OnCaptureIntervalChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_isUpdatingCaptureInterval
            || CaptureIntervalPicker.SelectedItem is not ComboBoxItem { Tag: string value }
            || !int.TryParse(value, CultureInfo.InvariantCulture, out var seconds)
            || seconds == ViewModel.CaptureIntervalSeconds)
        {
            return;
        }

        if (!await ViewModel.SetCaptureIntervalSecondsAsync(seconds))
        {
            SynchronizeCaptureInterval();
        }

        UpdateErrorInformation();
    }

    private async void OnRetentionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingPrivacyControls
            || RetentionPicker.SelectedItem is not ComboBoxItem { Tag: string value }
            || !int.TryParse(value, out var retentionDays)
            || retentionDays == ViewModel.EvidenceRetentionDays)
        {
            return;
        }

        if (!await ViewModel.SetEvidenceRetentionDaysAsync(retentionDays))
        {
            SynchronizePrivacyControls();
        }

        UpdateErrorInformation();
    }

    private async void OnAddProvider(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen || !RoutingViewModel.CanMutate)
        {
            return;
        }

        _editingProvider = null;
        ProviderEditorDialog.Title = "新增供应商";
        ProviderEditorDialog.PrimaryButtonText = "新增";
        ProviderEditorNameTextBox.Text = string.Empty;
        ProviderEditorEndpointTextBox.Text = string.Empty;
        ProviderEditorModelTextBox.Text = string.Empty;
        ProviderEditorTimeoutNumberBox.Value = 60;
        ProviderEditorConcurrencyNumberBox.Value = 1;
        ProviderEditorApiKeyPasswordBox.Password = string.Empty;
        ProviderEditorClearApiKeyCheckBox.IsChecked = false;
        ProviderEditorClearApiKeyCheckBox.Visibility = Visibility.Collapsed;
        ProviderEditorErrorInfoBar.IsOpen = false;
        _ = await ShowDialogAsync(ProviderEditorDialog);
    }

    private async void OnEditProvider(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen
            || !RoutingViewModel.CanMutate
            || ResolveProviderProfile(sender)
                is not AiProviderProfileItemViewModel profile)
        {
            return;
        }

        _editingProvider = profile;
        ProviderEditorDialog.Title = $"编辑“{profile.DisplayName}”";
        ProviderEditorDialog.PrimaryButtonText = "保存";
        ProviderEditorNameTextBox.Text = profile.DisplayName;
        ProviderEditorEndpointTextBox.Text = profile.BaseEndpoint;
        ProviderEditorModelTextBox.Text = profile.Model;
        ProviderEditorTimeoutNumberBox.Value = profile.RequestTimeoutSeconds;
        ProviderEditorConcurrencyNumberBox.Value = profile.MaximumConcurrency;
        ProviderEditorApiKeyPasswordBox.Password = string.Empty;
        ProviderEditorClearApiKeyCheckBox.IsChecked = false;
        ProviderEditorClearApiKeyCheckBox.Visibility = Visibility.Visible;
        ProviderEditorErrorInfoBar.IsOpen = false;
        _ = await ShowDialogAsync(ProviderEditorDialog);
    }

    private async void OnDeleteProvider(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen
            || !RoutingViewModel.CanMutate
            || ResolveProviderProfile(sender)
                is not AiProviderProfileItemViewModel profile)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"删除“{profile.DisplayName}”？",
            Content = CreateDialogText(
                "只有未被隐私检查或时间线分析引用的供应商可以删除。"),
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary)
        {
            _ = await RoutingViewModel.DeleteProfileAsync(profile);
            UpdateRoutingInformation();
        }
    }

    private async void OnValidateProviderPrivacy(object sender, RoutedEventArgs e)
    {
        if (ResolveProviderProfile(sender)
            is AiProviderProfileItemViewModel profile)
        {
            _ = await RoutingViewModel.ValidateStageAsync(
                profile,
                AnalysisStage.PrivacyInspection);
            UpdateRoutingInformation();
        }
    }

    private async void OnValidateProviderTimeline(object sender, RoutedEventArgs e)
    {
        if (ResolveProviderProfile(sender)
            is AiProviderProfileItemViewModel profile)
        {
            _ = await RoutingViewModel.ValidateStageAsync(
                profile,
                AnalysisStage.TimelineAnalysis);
            UpdateRoutingInformation();
        }
    }

    private async void OnSavePrivacyRoute(object sender, RoutedEventArgs e)
    {
        if (RoutingViewModel.PrivacyEnabled
            && RoutingViewModel.PrivacyProvider is null)
        {
            RoutingInfoBar.Title = "请选择隐私检查供应商";
            RoutingInfoBar.Message = "启用前必须为隐私检查选择一个供应商。";
            RoutingInfoBar.Severity = InfoBarSeverity.Warning;
            RoutingInfoBar.IsOpen = true;
            return;
        }

        _ = await RoutingViewModel.SavePrivacyBindingAsync();
        UpdateRoutingInformation();
    }

    private async void OnSaveTimelineRoute(object sender, RoutedEventArgs e)
    {
        if (RoutingViewModel.TimelineEnabled
            && RoutingViewModel.TimelineProvider is null)
        {
            RoutingInfoBar.Title = "请选择时间线分析供应商";
            RoutingInfoBar.Message = "启用前必须为时间线分析选择一个供应商。";
            RoutingInfoBar.Severity = InfoBarSeverity.Warning;
            RoutingInfoBar.IsOpen = true;
            return;
        }

        _ = await RoutingViewModel.SaveTimelineBindingAsync();
        UpdateRoutingInformation();
    }

    private async void OnProviderEditorPrimaryButtonClick(
        ContentDialog sender,
        ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            if (!TryReadProviderEditorTimeout(out var timeoutSeconds))
            {
                args.Cancel = true;
                ShowProviderEditorError("请求超时必须是 10 到 600 之间的整数秒数。");
                return;
            }

            if (!TryReadProviderEditorConcurrency(out var maximumConcurrency))
            {
                args.Cancel = true;
                ShowProviderEditorError("最大并发数必须是 1 到 16 之间的整数。");
                return;
            }

            ProviderEditorProgressBar.Visibility = Visibility.Visible;
            sender.IsPrimaryButtonEnabled = false;
            var saved = await RoutingViewModel.SaveProfileAsync(
                _editingProvider?.Id,
                _editingProvider?.Revision,
                ProviderEditorNameTextBox.Text.Trim(),
                ProviderEditorEndpointTextBox.Text.Trim(),
                ProviderEditorModelTextBox.Text.Trim(),
                timeoutSeconds,
                maximumConcurrency,
                ProviderEditorApiKeyPasswordBox.Password,
                ProviderEditorClearApiKeyCheckBox.IsChecked == true);
            if (!saved)
            {
                args.Cancel = true;
                ShowProviderEditorError(RoutingViewModel.ErrorMessage);
            }
        }
        finally
        {
            ProviderEditorApiKeyPasswordBox.Password = string.Empty;
            ProviderEditorProgressBar.Visibility = Visibility.Collapsed;
            sender.IsPrimaryButtonEnabled = true;
            UpdateRoutingInformation();
            deferral.Complete();
        }
    }

    private bool TryReadProviderEditorTimeout(out int timeoutSeconds)
    {
        var value = ProviderEditorTimeoutNumberBox.Value;
        timeoutSeconds = double.IsFinite(value) ? checked((int)value) : 0;
        return value == timeoutSeconds && timeoutSeconds is >= 10 and <= 600;
    }

    private bool TryReadProviderEditorConcurrency(out int maximumConcurrency)
    {
        var value = ProviderEditorConcurrencyNumberBox.Value;
        maximumConcurrency = double.IsFinite(value) ? checked((int)value) : 0;
        return value == maximumConcurrency
            && maximumConcurrency is >= 1 and <= AiProviderProfile.MaximumConcurrencyLimit;
    }

    private void ShowProviderEditorError(string message)
    {
        ProviderEditorErrorInfoBar.Message = string.IsNullOrWhiteSpace(message)
            ? "无法保存供应商配置，请检查输入。"
            : message;
        ProviderEditorErrorInfoBar.IsOpen = true;
    }


    private async void OnAddExclusionRule(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen || !ViewModel.CanAddExclusionRule)
        {
            return;
        }

        PrepareCreateExclusionRuleEditor();
        await ShowExclusionRuleEditorAsync();
    }

    private async void OnEditExclusionRule(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen
            || !ViewModel.CanChangeExclusionRules
            || !TryGetExclusionRuleItem(sender, out var item))
        {
            return;
        }

        PrepareEditExclusionRuleEditor(item);
        await ShowExclusionRuleEditorAsync();
    }

    private async void OnDeleteExclusionRule(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen
            || !ViewModel.CanChangeExclusionRules
            || !TryGetExclusionRuleItem(sender, out var item))
        {
            return;
        }

        var originalIndex = item.Index;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"删除“{item.Name}”？",
            Content = CreateDialogText(
                "删除后，这条规则不会再定义排除边界。若它当前已启用，隐私选择会更新，录制会保持关闭并需要重新确认授权。"),
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        bool deleted;
        string? fallbackError = null;
        try
        {
            deleted = await ViewModel.DeleteExclusionRuleAsync(item);
        }
        catch (Exception)
        {
            deleted = false;
            fallbackError = "无法删除排除规则，请稍后重试。";
        }

        UpdateErrorInformation();
        UpdateExclusionRuleInformation();
        if (fallbackError is not null)
        {
            ShowPageError(fallbackError);
        }

        if (!deleted)
        {
            FocusExclusionRuleControl(item.Id);
            return;
        }

        await Task.Yield();
        if (ViewModel.ExclusionRules.Count == 0)
        {
            AddExclusionRuleButton.Focus(FocusState.Programmatic);
            return;
        }

        var nextIndex = Math.Min(originalIndex, ViewModel.ExclusionRules.Count - 1);
        FocusExclusionRuleControl(ViewModel.ExclusionRules[nextIndex].Id);
    }

    private async void OnExclusionRuleToggled(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingExclusionRuleControls
            || sender is not ToggleSwitch toggle
            || !TryGetExclusionRuleItem(sender, out var item)
            || toggle.IsOn == item.IsEnabled)
        {
            return;
        }

        var changed = false;
        string? fallbackError = null;
        if (ViewModel.CanChangeExclusionRules)
        {
            try
            {
                changed = await ViewModel.SetExclusionRuleEnabledAsync(item, toggle.IsOn);
            }
            catch (Exception)
            {
                fallbackError = "无法更改排除规则，请稍后重试。";
            }
        }

        if (!changed || toggle.IsOn != item.IsEnabled)
        {
            _isUpdatingExclusionRuleControls = true;
            toggle.IsOn = item.IsEnabled;
            _isUpdatingExclusionRuleControls = false;
        }

        UpdateErrorInformation();
        UpdateExclusionRuleInformation();
        if (fallbackError is not null)
        {
            ShowPageError(fallbackError);
        }
    }

    private async void OnMoveExclusionRuleUp(object sender, RoutedEventArgs e)
    {
        await MoveExclusionRuleAsync(sender, offset: -1);
    }

    private async void OnMoveExclusionRuleDown(object sender, RoutedEventArgs e)
    {
        await MoveExclusionRuleAsync(sender, offset: 1);
    }

    private async Task MoveExclusionRuleAsync(object sender, int offset)
    {
        if (!ViewModel.CanChangeExclusionRules
            || !TryGetExclusionRuleItem(sender, out var item))
        {
            return;
        }

        var moved = false;
        string? fallbackError = null;
        try
        {
            moved = await ViewModel.MoveExclusionRuleAsync(item, offset);
        }
        catch (Exception)
        {
            fallbackError = "无法调整排除规则顺序，请稍后重试。";
        }

        UpdateErrorInformation();
        UpdateExclusionRuleInformation();
        if (fallbackError is not null)
        {
            ShowPageError(fallbackError);
        }

        if (!moved)
        {
            return;
        }

        await Task.Yield();
        FocusExclusionRuleControl(
            item.Id,
            offset < 0 ? "MoveExclusionRuleUpButton" : "MoveExclusionRuleDownButton",
            offset < 0 ? "MoveExclusionRuleDownButton" : "MoveExclusionRuleUpButton",
            "EditExclusionRuleButton");
    }

    private void OnExclusionRuleRowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Control rowRoot)
        {
            ApplyExclusionRuleRowLayout(rowRoot);
        }
    }

    private void OnExclusionRuleEditorSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        UpdateExclusionRuleEditorFields();
    }

    private void OnExclusionRuleMutationInfoBarClosed(
        InfoBar sender,
        InfoBarClosedEventArgs args)
    {
        ViewModel.ClearRuleMutationNotice();
    }

    private async void OnExclusionRuleEditorPrimaryButtonClick(
        ContentDialog sender,
        ContentDialogButtonClickEventArgs args)
    {
        if (!TryReadExclusionRuleEditor(out var values))
        {
            args.Cancel = true;
            return;
        }

        var deferral = args.GetDeferral();
        SetExclusionRuleEditorSavingState(saving: true);
        try
        {
            var saved = _editingExclusionRule is null
                ? await ViewModel.AddExclusionRuleAsync(
                    values.Name,
                    values.Enabled,
                    values.Scope,
                    values.ApplicationIdentityKind,
                    values.IdentityValue,
                    values.WindowTitleMatchKind,
                    values.Pattern)
                : await ViewModel.UpdateExclusionRuleAsync(
                    _editingExclusionRule,
                    values.Name,
                    values.Scope,
                    values.ApplicationIdentityKind,
                    values.IdentityValue,
                    values.WindowTitleMatchKind,
                    values.Pattern);
            if (!saved)
            {
                args.Cancel = true;
                ShowExclusionRuleEditorError(
                    ViewModel.HasError
                        ? ViewModel.ErrorMessage
                        : "无法保存排除规则，请稍后重试。",
                    "保存失败");
                return;
            }

            var focusId = _editingExclusionRule?.Id
                ?? ViewModel.ExclusionRules.LastOrDefault()?.Id;
            if (focusId is not null)
            {
                ExclusionRuleEditorDialog.Tag = focusId.Value;
            }
        }
        catch (Exception)
        {
            args.Cancel = true;
            ShowExclusionRuleEditorError("无法保存排除规则，请稍后重试。", "保存失败");
        }
        finally
        {
            SetExclusionRuleEditorSavingState(saving: false);
            deferral.Complete();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.Theme))
        {
            SynchronizeThemePicker();
        }

        if (e.PropertyName == nameof(SettingsViewModel.CaptureEnabled))
        {
            SynchronizeCaptureToggle();
        }

        if (e.PropertyName == nameof(SettingsViewModel.CaptureIntervalSeconds))
        {
            SynchronizeCaptureInterval();
        }

        if (e.PropertyName == nameof(SettingsViewModel.EvidenceRetentionDays))
        {
            SynchronizePrivacyControls();
        }

        if (e.PropertyName is nameof(SettingsViewModel.HasValidRecordingConsent)
            or nameof(SettingsViewModel.CanGrantConsent)
            or nameof(SettingsViewModel.CanRevokeConsent))
        {
            UpdateConsentActions();
        }

        if (e.PropertyName is nameof(SettingsViewModel.CaptureAvailabilityText)
            or nameof(SettingsViewModel.IsCaptureBackendAvailable)
            or nameof(SettingsViewModel.HasValidRecordingConsent))
        {
            UpdateCaptureInformation();
        }

        if (e.PropertyName is nameof(SettingsViewModel.ErrorMessage)
            or nameof(SettingsViewModel.HasError))
        {
            UpdateErrorInformation();
        }

        if (e.PropertyName is nameof(SettingsViewModel.HasExclusionRules)
            or nameof(SettingsViewModel.ExclusionRuleCount)
            or nameof(SettingsViewModel.EnabledExclusionRuleCount)
            or nameof(SettingsViewModel.ExclusionRuleSummaryText)
            or nameof(SettingsViewModel.ExclusionEngineStatusText)
            or nameof(SettingsViewModel.RuleMutationNoticeText)
            or nameof(SettingsViewModel.HasRuleMutationNotice))
        {
            UpdateExclusionRuleInformation();
        }

        if (e.PropertyName == nameof(SettingsViewModel.IsBusy))
        {
            ThemePicker.IsEnabled = !ViewModel.IsBusy;
        }
    }

    private void OnRoutingViewModelPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        UpdateRoutingInformation();
    }

    private void PrepareCreateExclusionRuleEditor()
    {
        _editingExclusionRule = null;
        ExclusionRuleEditorDialog.Title = "添加排除规则";
        ExclusionRuleEditorDialog.PrimaryButtonText = "添加";
        ExclusionRuleEditorDialog.Tag = null;
        ExclusionRuleNameTextBox.Text = string.Empty;
        ExclusionRuleIdentityTextBox.Text = string.Empty;
        ExclusionRuleWindowPatternTextBox.Text = string.Empty;
        ExclusionRuleEnabledToggle.Visibility = Visibility.Visible;
        ExclusionRuleEnabledToggle.IsOn = true;
        SelectComboBoxValue(ExclusionRuleScopePicker, CaptureExclusionRuleScope.Application);
        SelectComboBoxValue(
            ExclusionRuleIdentityKindPicker,
            ApplicationIdentityKind.ExecutableName);
        SelectComboBoxValue(
            ExclusionRuleWindowMatchKindPicker,
            WindowTitleMatchKind.Contains);
        ResetExclusionRuleEditorState();
        UpdateExclusionRuleEditorFields();
    }

    private void PrepareEditExclusionRuleEditor(ExclusionRuleItemViewModel item)
    {
        _editingExclusionRule = item;
        ExclusionRuleEditorDialog.Title = "编辑排除规则";
        ExclusionRuleEditorDialog.PrimaryButtonText = "保存";
        ExclusionRuleEditorDialog.Tag = null;
        ExclusionRuleNameTextBox.Text = item.Name;
        ExclusionRuleIdentityTextBox.Text = item.IdentityValue;
        ExclusionRuleWindowPatternTextBox.Text = item.Pattern ?? string.Empty;
        ExclusionRuleEnabledToggle.Visibility = Visibility.Collapsed;
        ExclusionRuleEnabledToggle.IsOn = item.IsEnabled;
        SelectComboBoxValue(ExclusionRuleScopePicker, item.Scope);
        SelectComboBoxValue(
            ExclusionRuleIdentityKindPicker,
            item.ApplicationIdentityKind);
        SelectComboBoxValue(
            ExclusionRuleWindowMatchKindPicker,
            item.WindowTitleMatchKind ?? WindowTitleMatchKind.Contains);
        ResetExclusionRuleEditorState();
        UpdateExclusionRuleEditorFields();
    }

    private async Task ShowExclusionRuleEditorAsync()
    {
        _dialogOpen = true;
        try
        {
            ExclusionRuleEditorDialog.XamlRoot = XamlRoot;
            await ExclusionRuleEditorDialog.ShowAsync();
        }
        catch (Exception)
        {
            ShowPageError("无法打开排除规则编辑器，请稍后重试。");
        }
        finally
        {
            _dialogOpen = false;
            SetExclusionRuleEditorSavingState(saving: false);
        }

        if (ExclusionRuleEditorDialog.Tag is Guid focusId)
        {
            ExclusionRuleEditorDialog.Tag = null;
            await Task.Yield();
            FocusExclusionRuleControl(focusId);
        }
    }

    private void UpdateExclusionRuleEditorFields()
    {
        var isWindowRule = TryGetSelectedEnum(
                ExclusionRuleScopePicker,
                out CaptureExclusionRuleScope scope)
            && scope == CaptureExclusionRuleScope.Window;
        ExclusionRuleWindowMatchPanel.Visibility = isWindowRule
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!TryGetSelectedEnum(
                ExclusionRuleIdentityKindPicker,
                out ApplicationIdentityKind identityKind))
        {
            return;
        }

        switch (identityKind)
        {
            case ApplicationIdentityKind.ExecutableName:
                ExclusionRuleIdentityTextBox.Header = "可执行文件名";
                ExclusionRuleIdentityTextBox.PlaceholderText = "例如：KeePassXC.exe";
                ExclusionRuleIdentityTextBox.MaxLength =
                    CaptureExclusionRule.MaximumExecutableNameLength;
                ExclusionRuleIdentityHelpText.Text =
                    "只接受以 .exe 结尾的文件名，不接受完整路径、驱动器号或通配符。";
                AutomationProperties.SetName(ExclusionRuleIdentityTextBox, "可执行文件名");
                break;
            case ApplicationIdentityKind.PackageFamilyName:
                ExclusionRuleIdentityTextBox.Header = "包系列名称 (PFN)";
                ExclusionRuleIdentityTextBox.PlaceholderText =
                    "例如：Publisher.Application_abc123def4567";
                ExclusionRuleIdentityTextBox.MaxLength =
                    CaptureExclusionRule.MaximumPackageFamilyNameLength;
                ExclusionRuleIdentityHelpText.Text =
                    "输入包名称、下划线和 13 位发布者 ID；不读取当前运行的窗口。";
                AutomationProperties.SetName(ExclusionRuleIdentityTextBox, "包系列名称");
                break;
            case ApplicationIdentityKind.PublisherCertificateSha256:
                ExclusionRuleIdentityTextBox.Header = "发布者证书 SHA-256";
                ExclusionRuleIdentityTextBox.PlaceholderText = "输入 64 位十六进制摘要";
                ExclusionRuleIdentityTextBox.MaxLength =
                    CaptureExclusionRule.PublisherCertificateSha256Length;
                ExclusionRuleIdentityHelpText.Text =
                    "输入发布者签名证书的 SHA-256 摘要，不接受证书文件路径。";
                AutomationProperties.SetName(
                    ExclusionRuleIdentityTextBox,
                    "发布者证书 SHA-256");
                break;
        }
    }

    private bool TryReadExclusionRuleEditor(out ExclusionRuleEditorValues values)
    {
        values = null!;
        var nameInput = ExclusionRuleNameTextBox.Text;
        if (nameInput.Any(char.IsControl))
        {
            ShowExclusionRuleEditorError("规则名称应为 1 到 80 个不含控制字符的文字。");
            ExclusionRuleNameTextBox.Focus(FocusState.Programmatic);
            return false;
        }

        var name = nameInput.Trim();
        if (name.Length == 0
            || name.Length > CaptureExclusionRule.MaximumNameLength)
        {
            ShowExclusionRuleEditorError("规则名称应为 1 到 80 个不含控制字符的文字。");
            ExclusionRuleNameTextBox.Focus(FocusState.Programmatic);
            return false;
        }

        if (!TryGetSelectedEnum(
                ExclusionRuleScopePicker,
                out CaptureExclusionRuleScope scope))
        {
            ShowExclusionRuleEditorError("请选择排除范围。");
            ExclusionRuleScopePicker.Focus(FocusState.Programmatic);
            return false;
        }

        if (!TryGetSelectedEnum(
                ExclusionRuleIdentityKindPicker,
                out ApplicationIdentityKind identityKind))
        {
            ShowExclusionRuleEditorError("请选择应用身份类型。");
            ExclusionRuleIdentityKindPicker.Focus(FocusState.Programmatic);
            return false;
        }

        if (!IsValidApplicationIdentity(
                identityKind,
                ExclusionRuleIdentityTextBox.Text,
                out var identity,
                out var identityError))
        {
            ShowExclusionRuleEditorError(identityError);
            ExclusionRuleIdentityTextBox.Focus(FocusState.Programmatic);
            return false;
        }

        WindowTitleMatchKind? windowTitleMatchKind = null;
        string? pattern = null;
        if (scope == CaptureExclusionRuleScope.Window)
        {
            if (!TryGetSelectedEnum(
                    ExclusionRuleWindowMatchKindPicker,
                    out WindowTitleMatchKind selectedMatchKind))
            {
                ShowExclusionRuleEditorError("请选择窗口标题匹配方式。");
                ExclusionRuleWindowMatchKindPicker.Focus(FocusState.Programmatic);
                return false;
            }

            pattern = ExclusionRuleWindowPatternTextBox.Text;
            if (string.IsNullOrWhiteSpace(pattern)
                || pattern.Length < 2
                || pattern.Length > CaptureExclusionRule.MaximumWindowTitlePatternLength
                || pattern.Any(char.IsControl))
            {
                ShowExclusionRuleEditorError("匹配文字应为 2 到 256 个不含控制字符的文字。");
                ExclusionRuleWindowPatternTextBox.Focus(FocusState.Programmatic);
                return false;
            }

            windowTitleMatchKind = selectedMatchKind;
        }

        values = new ExclusionRuleEditorValues(
            name,
            ExclusionRuleEnabledToggle.IsOn,
            scope,
            identityKind,
            identity,
            windowTitleMatchKind,
            pattern);
        ExclusionRuleEditorErrorInfoBar.IsOpen = false;
        return true;
    }

    private static bool IsValidApplicationIdentity(
        ApplicationIdentityKind identityKind,
        string identityInput,
        out string identity,
        out string error)
    {
        if (CaptureExclusionRule.TryNormalizeApplicationIdentity(
                identityKind,
                identityInput,
                out identity))
        {
            error = string.Empty;
            return true;
        }

        error = identityKind switch
        {
            ApplicationIdentityKind.ExecutableName =>
                "请输入以 .exe 结尾的文件名，不要输入完整路径、驱动器号或通配符。",
            ApplicationIdentityKind.PackageFamilyName =>
                "包系列名称应由 3 到 50 位包名称、下划线和 13 位发布者 ID 组成。",
            ApplicationIdentityKind.PublisherCertificateSha256 =>
                "发布者证书 SHA-256 必须是 64 位十六进制摘要。",
            _ => "不支持此应用身份类型。",
        };
        return false;
    }

    private void ResetExclusionRuleEditorState()
    {
        ExclusionRuleEditorErrorInfoBar.IsOpen = false;
        ExclusionRuleEditorErrorInfoBar.Message = string.Empty;
        SetExclusionRuleEditorSavingState(saving: false);
    }

    private void SetExclusionRuleEditorSavingState(bool saving)
    {
        ExclusionRuleEditorDialog.IsPrimaryButtonEnabled = !saving;
        ExclusionRuleEditorDialog.IsSecondaryButtonEnabled = !saving;
        ExclusionRuleNameTextBox.IsEnabled = !saving;
        ExclusionRuleScopePicker.IsEnabled = !saving;
        ExclusionRuleIdentityKindPicker.IsEnabled = !saving;
        ExclusionRuleIdentityTextBox.IsEnabled = !saving;
        ExclusionRuleWindowMatchKindPicker.IsEnabled = !saving;
        ExclusionRuleWindowPatternTextBox.IsEnabled = !saving;
        ExclusionRuleEnabledToggle.IsEnabled = !saving;
        ExclusionRuleEditorProgressBar.Visibility = saving
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ShowExclusionRuleEditorError(
        string message,
        string title = "请检查规则信息")
    {
        ExclusionRuleEditorErrorInfoBar.Title = title;
        ExclusionRuleEditorErrorInfoBar.Message = message;
        ExclusionRuleEditorErrorInfoBar.IsOpen = true;
    }

    private void UpdateExclusionRuleInformation()
    {
        ExclusionRuleSummaryTextBlock.Text = ViewModel.ExclusionRuleSummaryText;
        ExclusionRuleAvailabilityTextBlock.Text = ViewModel.ExclusionEngineStatusText;
        ExclusionRuleEmptyState.Visibility = ViewModel.HasExclusionRules
            ? Visibility.Collapsed
            : Visibility.Visible;
        ExclusionRuleList.Visibility = ViewModel.HasExclusionRules
            ? Visibility.Visible
            : Visibility.Collapsed;
        ExclusionRuleMutationInfoBar.Message = ViewModel.RuleMutationNoticeText;
        ExclusionRuleMutationInfoBar.IsOpen = ViewModel.HasRuleMutationNotice;
    }

    private static bool TryGetExclusionRuleItem(
        object sender,
        out ExclusionRuleItemViewModel item)
    {
        item = (sender as FrameworkElement)?.DataContext as ExclusionRuleItemViewModel
            ?? null!;
        return item is not null;
    }

    private void FocusExclusionRuleControl(Guid id, params string[] preferredControlNames)
    {
        var row = FindExclusionRuleRow(ExclusionRuleList, id);
        if (row is not null)
        {
            var controlNames = preferredControlNames.Length == 0
                ? ["EditExclusionRuleButton"]
                : preferredControlNames;
            foreach (var controlName in controlNames)
            {
                var control = FindNamedDescendant<Control>(row, controlName);
                if (control is { IsEnabled: true, Visibility: Visibility.Visible }
                    && control.Focus(FocusState.Programmatic))
                {
                    return;
                }
            }
        }

        if (!ExclusionRuleList.Focus(FocusState.Programmatic))
        {
            AddExclusionRuleButton.Focus(FocusState.Programmatic);
        }
    }

    private static Control? FindExclusionRuleRow(DependencyObject root, Guid id)
    {
        if (root is Control { Name: "ExclusionRuleRowRoot", DataContext: ExclusionRuleItemViewModel item }
            && item.Id == id)
        {
            return (Control)root;
        }

        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            var match = FindExclusionRuleRow(VisualTreeHelper.GetChild(root, index), id);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static bool TryGetSelectedEnum<TEnum>(ComboBox picker, out TEnum value)
        where TEnum : struct, Enum
    {
        value = default;
        return picker.SelectedItem is ComboBoxItem { Tag: string tag }
            && Enum.TryParse(tag, out value)
            && Enum.IsDefined(value);
    }

    private static void SelectComboBoxValue<TEnum>(ComboBox picker, TEnum value)
        where TEnum : struct, Enum
    {
        var tag = value.ToString();
        picker.SelectedItem = picker.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(
                item.Tag as string,
                tag,
                StringComparison.Ordinal));
    }

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        _dialogOpen = true;
        try
        {
            return await dialog.ShowAsync();
        }
        catch (Exception)
        {
            ShowPageError("无法打开确认窗口，请稍后重试。");
            return ContentDialogResult.None;
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private void SynchronizeThemePicker()
    {
        _isUpdatingThemePicker = true;
        ThemePicker.SelectedIndex = ViewModel.Theme switch
        {
            AppThemePreference.Light => 1,
            AppThemePreference.Dark => 2,
            _ => 0,
        };
        _isUpdatingThemePicker = false;
    }

    private void SynchronizeCaptureToggle()
    {
        _isUpdatingCaptureToggle = true;
        CaptureToggle.IsOn = ViewModel.CaptureEnabled;
        _isUpdatingCaptureToggle = false;
    }

    private void SynchronizeCaptureInterval()
    {
        _isUpdatingCaptureInterval = true;
        try
        {
            var value = ViewModel.CaptureIntervalSeconds.ToString(
                CultureInfo.InvariantCulture);
            CaptureIntervalPicker.SelectedItem = CaptureIntervalPicker.Items
                .OfType<ComboBoxItem>()
                .First(item => string.Equals(
                    item.Tag as string,
                    value,
                    StringComparison.Ordinal));
        }
        finally
        {
            _isUpdatingCaptureInterval = false;
        }
    }

    private void SynchronizePrivacyControls()
    {
        _isUpdatingPrivacyControls = true;
        try
        {
            var retentionTag = ViewModel.EvidenceRetentionDays.ToString(
                CultureInfo.InvariantCulture);
            var matchingItem = RetentionPicker.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Tag as string,
                    retentionTag,
                    StringComparison.Ordinal));
            if (matchingItem is null)
            {
                matchingItem = new ComboBoxItem
                {
                    Content = ViewModel.EvidenceRetentionDays
                        == EvidenceSettings.UnlimitedRetentionDays
                            ? "不自动清理"
                            : $"{ViewModel.EvidenceRetentionDays} 天",
                    Tag = retentionTag,
                };
                RetentionPicker.Items.Add(matchingItem);
            }

            RetentionPicker.SelectedItem = matchingItem;
        }
        finally
        {
            _isUpdatingPrivacyControls = false;
        }
    }

    private void UpdateRoutingInformation()
    {
        ProviderEmptyState.Visibility = RoutingViewModel.HasProfiles
            ? Visibility.Collapsed
            : Visibility.Visible;
        ProviderList.Visibility = RoutingViewModel.HasProfiles
            ? Visibility.Visible
            : Visibility.Collapsed;

        var title = string.Empty;
        var message = string.Empty;
        var severity = InfoBarSeverity.Informational;
        var isOpen = false;
        if (RoutingViewModel.HasError)
        {
            title = "供应商或阶段设置失败";
            message = RoutingViewModel.ErrorMessage;
            severity = InfoBarSeverity.Error;
            isOpen = true;
        }
        else if (RoutingViewModel.HasNotice)
        {
            title = "设置已更新";
            message = RoutingViewModel.NoticeMessage;
            severity = InfoBarSeverity.Success;
            isOpen = true;
        }

        RoutingInfoBar.Title = title;
        RoutingInfoBar.Message = message;
        RoutingInfoBar.Severity = severity;
        RoutingInfoBar.IsOpen = isOpen;
        ProviderRoutingInfoBar.Title = title;
        ProviderRoutingInfoBar.Message = message;
        ProviderRoutingInfoBar.Severity = severity;
        ProviderRoutingInfoBar.IsOpen = isOpen;
    }

    private void UpdateConsentActions()
    {
        GrantConsentButton.Visibility = ViewModel.HasValidRecordingConsent
            ? Visibility.Collapsed
            : Visibility.Visible;
        RevokeConsentButton.Visibility = ViewModel.HasValidRecordingConsent
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateCaptureInformation()
    {
        if (!ViewModel.IsCaptureBackendAvailable)
        {
            CaptureInfoBar.Title = "录制不可用";
            CaptureInfoBar.Severity = InfoBarSeverity.Informational;
        }
        else if (!ViewModel.HasValidRecordingConsent)
        {
            CaptureInfoBar.Title = "等待录制授权";
            CaptureInfoBar.Severity = InfoBarSeverity.Warning;
        }
        else
        {
            CaptureInfoBar.Title = "录制状态";
            CaptureInfoBar.Severity = InfoBarSeverity.Informational;
        }
    }

    private void UpdateErrorInformation()
    {
        SettingsErrorInfoBar.Message = ViewModel.ErrorMessage;
        SettingsErrorInfoBar.IsOpen = ViewModel.HasError;
    }

    private void ShowPageError(string message)
    {
        SettingsErrorInfoBar.Message = message;
        SettingsErrorInfoBar.IsOpen = true;
    }

    private void UpdateResponsiveLayout(double width)
    {
        if (width <= 0)
        {
            return;
        }

        var useStackedLayout = width <= StackedLayoutMaximumWidth;
        UpdateSettingLayout(ThemeSettingLayout, ThemePicker, useStackedLayout);
        UpdateSettingLayout(ConsentSettingLayout, ConsentActionPanel, useStackedLayout);
        UpdateSettingLayout(CaptureSettingLayout, CaptureToggle, useStackedLayout);
        UpdateSettingLayout(
            CaptureIntervalSettingLayout,
            CaptureIntervalPicker,
            useStackedLayout);
        UpdateSettingLayout(
            StorageSettingLayout,
            DataFolderControlLayout,
            useStackedLayout);
        UpdateSettingLayout(RetentionSettingLayout, RetentionPicker, useStackedLayout);
        UpdateSettingLayout(PrivacyHeaderLayout, PrivacyToggle, useStackedLayout);
        UpdateSettingLayout(
            PrivacyPolicyLayout,
            PrivacyOnErrorPicker,
            useStackedLayout);
        UpdateSettingLayout(TimelineHeaderLayout, TimelineToggle, useStackedLayout);
        UpdateSettingLayout(
            ExclusionRulesHeaderLayout,
            AddExclusionRuleButton,
            useStackedLayout);
        UpdateSettingLayout(
            ProviderHeaderLayout,
            AddProviderButton,
            useStackedLayout);

        ConsentActionPanel.Orientation = useStackedLayout
            ? Orientation.Vertical
            : Orientation.Horizontal;
        GrantConsentButton.HorizontalAlignment = useStackedLayout
            ? HorizontalAlignment.Stretch
            : HorizontalAlignment.Left;
        RevokeConsentButton.HorizontalAlignment = useStackedLayout
            ? HorizontalAlignment.Stretch
            : HorizontalAlignment.Left;

        if (_useStackedExclusionRuleLayout != useStackedLayout)
        {
            _useStackedExclusionRuleLayout = useStackedLayout;
            ApplyRealizedExclusionRuleLayouts(ExclusionRuleList);
        }
    }

    private void ApplyRealizedExclusionRuleLayouts(DependencyObject root)
    {
        if (root is Control { Name: "ExclusionRuleRowRoot" } rowRoot)
        {
            ApplyExclusionRuleRowLayout(rowRoot);
        }

        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            ApplyRealizedExclusionRuleLayouts(VisualTreeHelper.GetChild(root, index));
        }
    }

    private void ApplyExclusionRuleRowLayout(Control rowRoot)
    {
        var grid = FindNamedDescendant<Grid>(rowRoot, "ExclusionRuleRowGrid");
        var content = FindNamedDescendant<StackPanel>(rowRoot, "ExclusionRuleContentStack");
        var actions = FindNamedDescendant<Grid>(rowRoot, "ExclusionRuleActionPanel");
        var stateActions = FindNamedDescendant<StackPanel>(
            rowRoot,
            "ExclusionRuleStateActionPanel");
        var editActions = FindNamedDescendant<StackPanel>(
            rowRoot,
            "ExclusionRuleEditActionPanel");
        if (grid is null
            || content is null
            || actions is null
            || stateActions is null
            || editActions is null)
        {
            return;
        }

        grid.ColumnSpacing = _useStackedExclusionRuleLayout ? 0 : 8;
        grid.RowSpacing = _useStackedExclusionRuleLayout ? 12 : 0;
        Grid.SetRow(content, 0);
        Grid.SetColumn(content, 0);
        Grid.SetColumnSpan(content, _useStackedExclusionRuleLayout ? 2 : 1);
        Grid.SetRow(actions, _useStackedExclusionRuleLayout ? 1 : 0);
        Grid.SetColumn(actions, _useStackedExclusionRuleLayout ? 0 : 1);
        Grid.SetColumnSpan(actions, _useStackedExclusionRuleLayout ? 2 : 1);
        actions.ColumnSpacing = _useStackedExclusionRuleLayout ? 0 : 8;
        actions.RowSpacing = _useStackedExclusionRuleLayout ? 4 : 0;
        actions.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetRow(stateActions, 0);
        Grid.SetColumn(stateActions, 0);
        Grid.SetColumnSpan(stateActions, _useStackedExclusionRuleLayout ? 2 : 1);
        Grid.SetRow(editActions, _useStackedExclusionRuleLayout ? 1 : 0);
        Grid.SetColumn(editActions, _useStackedExclusionRuleLayout ? 0 : 1);
        Grid.SetColumnSpan(editActions, _useStackedExclusionRuleLayout ? 2 : 1);
    }

    private static T? FindNamedDescendant<T>(DependencyObject root, string name)
        where T : FrameworkElement
    {
        if (root is T { Name: var elementName } element
            && string.Equals(elementName, name, StringComparison.Ordinal))
        {
            return element;
        }

        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            var match = FindNamedDescendant<T>(VisualTreeHelper.GetChild(root, index), name);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static void UpdateSettingLayout(
        Grid layout,
        FrameworkElement control,
        bool useStackedLayout)
    {
        layout.RowSpacing = useStackedLayout ? 12 : 0;
        Grid.SetRow(control, useStackedLayout ? 1 : 0);
        Grid.SetColumn(control, useStackedLayout ? 0 : 1);
        Grid.SetColumnSpan(control, useStackedLayout ? 2 : 1);
        control.HorizontalAlignment = useStackedLayout
            ? HorizontalAlignment.Stretch
            : control is ComboBox or TextBox or Grid
                ? HorizontalAlignment.Stretch
                : HorizontalAlignment.Right;
    }

    private AiProviderProfileItemViewModel? ResolveProviderProfile(object sender)
    {
        if (sender is not FrameworkElement element)
        {
            return null;
        }

        if (element.DataContext is AiProviderProfileItemViewModel profile)
        {
            return profile;
        }

        return element.Tag is Guid profileId
            ? RoutingViewModel.Profiles.FirstOrDefault(
                candidate => candidate.Id == profileId)
            : null;
    }

    private static string GetApplicationDisplayVersion()
    {
        var assembly = typeof(SettingsPage).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion.Split('+', 2)[0];
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    private static TextBlock CreateDialogText(string text)
    {
        return new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
        };
    }

    private sealed record ExclusionRuleEditorValues(
        string Name,
        bool Enabled,
        CaptureExclusionRuleScope Scope,
        ApplicationIdentityKind ApplicationIdentityKind,
        string IdentityValue,
        WindowTitleMatchKind? WindowTitleMatchKind,
        string? Pattern);
}
