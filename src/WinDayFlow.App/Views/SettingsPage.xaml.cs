using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinDayFlow.Application.Settings;
using WinDayFlow.Presentation.Settings;

namespace WinDayFlow.App.Views;

public sealed partial class SettingsPage : Page
{
    private const double StackedLayoutMaximumWidth = 620;

    private bool _dialogOpen;
    private ExclusionRuleItemViewModel? _editingExclusionRule;
    private bool _isSubscribed;
    private bool _isUpdatingCloudAnalysisToggle;
    private bool _isUpdatingCaptureToggle;
    private bool _isUpdatingExclusionRuleControls;
    private bool _isUpdatingPrivacyControls;
    private bool _isUpdatingThemePicker;
    private bool _useStackedExclusionRuleLayout;

    public SettingsPage()
    {
        ViewModel = App.GetService<SettingsViewModel>();
        AiViewModel = App.GetService<AiProviderSettingsViewModel>();
        InitializeComponent();
        DataFolderTextBox.Text = App.DataDirectoryPath;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        SynchronizeThemePicker();
        SynchronizeCaptureToggle();
        SynchronizePrivacyControls();
        UpdateConsentActions();
        UpdateCaptureInformation();
        UpdateExclusionRuleInformation();
        SynchronizeAiProviderControls();
        UpdateAiProviderInformation();
    }

    public SettingsViewModel ViewModel { get; }

    public AiProviderSettingsViewModel AiViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_isSubscribed)
        {
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            AiViewModel.PropertyChanged += OnAiViewModelPropertyChanged;
            _isSubscribed = true;
        }

        UpdateResponsiveLayout(ActualWidth);
        SynchronizeThemePicker();
        SynchronizeCaptureToggle();
        SynchronizePrivacyControls();
        UpdateConsentActions();
        UpdateCaptureInformation();
        UpdateExclusionRuleInformation();
        UpdateErrorInformation();
        SynchronizeAiProviderControls();
        UpdateAiProviderInformation();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_isSubscribed)
        {
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            AiViewModel.PropertyChanged -= OnAiViewModelPropertyChanged;
            _isSubscribed = false;
        }

        SizeChanged -= OnSizeChanged;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        ViewModel.Dispose();
        AiViewModel.Dispose();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateResponsiveLayout(e.NewSize.Width);
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

    private async void OnRetentionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingPrivacyControls
            || RetentionPicker.SelectedItem is not ComboBoxItem { Tag: string value }
            || !int.TryParse(value, out var retentionDays)
            || retentionDays == ViewModel.EvidenceRetentionDays)
        {
            return;
        }

        await PersistPrivacyControlsAsync(retentionDays);
    }

    private async void OnPrivacyToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingPrivacyControls)
        {
            return;
        }

        await PersistPrivacyControlsAsync(ViewModel.EvidenceRetentionDays);
    }

    private async Task PersistPrivacyControlsAsync(int retentionDays)
    {
        if (!await ViewModel.SetCapturePrivacyAsync(
                retentionDays,
                SensitiveApplicationToggle.IsOn,
                RemoteSessionToggle.IsOn,
                ScreenSharingToggle.IsOn))
        {
            SynchronizePrivacyControls();
        }

        UpdateErrorInformation();
    }

    private async void OnSaveAiProvider(object sender, RoutedEventArgs e)
    {
        if (AiViewModel.IsBusy)
        {
            return;
        }

        if (!TryReadAiProviderTimeout(out var requestTimeoutSeconds))
        {
            ShowAiProviderError("请求超时必须是 10 到 600 之间的整数秒数。");
            AiProviderTimeoutNumberBox.Focus(FocusState.Programmatic);
            return;
        }

        var apiKey = AiProviderApiKeyPasswordBox.Password;
        try
        {
            var saved = await AiViewModel.SaveAsync(
                AiProviderNameTextBox.Text.Trim(),
                AiProviderEndpointTextBox.Text.Trim(),
                AiProviderModelTextBox.Text.Trim(),
                requestTimeoutSeconds,
                apiKey,
                ClearAiProviderApiKeyCheckBox.IsChecked == true);
            if (saved)
            {
                SynchronizeAiProviderControls();
            }
        }
        finally
        {
            AiProviderApiKeyPasswordBox.Password = string.Empty;
        }

        UpdateAiProviderInformation();
    }

    private async void OnTestAiProvider(object sender, RoutedEventArgs e)
    {
        if (AiViewModel.IsBusy)
        {
            return;
        }

        _ = await AiViewModel.TestConnectionAsync();
        UpdateAiProviderInformation();
    }

    private async void OnCloudAnalysisToggled(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingCloudAnalysisToggle
            || AiViewModel.IsBusy
            || CloudAnalysisToggle.IsOn == AiViewModel.CloudAnalysisEnabled)
        {
            return;
        }

        var requestedState = CloudAnalysisToggle.IsOn;
        if (requestedState && !await ConfirmCloudAnalysisDisclosureAsync())
        {
            SynchronizeCloudAnalysisToggle();
            return;
        }

        if (!await AiViewModel.SetCloudAnalysisEnabledAsync(requestedState))
        {
            SynchronizeCloudAnalysisToggle();
        }

        UpdateAiProviderInformation();
    }

    private async Task<bool> ConfirmCloudAnalysisDisclosureAsync()
    {
        if (_dialogOpen)
        {
            return false;
        }

        var endpointOrigin = Uri.TryCreate(
            AiViewModel.BaseEndpoint,
            UriKind.Absolute,
            out var endpoint)
                ? endpoint.GetLeftPart(UriPartial.Authority)
                : AiViewModel.BaseEndpoint;
        var content = new StackPanel
        {
            MaxWidth = 520,
            Spacing = 10,
        };
        content.Children.Add(CreateDialogText($"接收方：{endpointOrigin}"));
        content.Children.Add(CreateDialogText(
            "发送内容：从已提交录制块提取的少量静态截图、对应时间范围，以及经过隐私规则筛选的应用上下文。"));
        content.Children.Add(CreateDialogText(
            "不会发送：完整录制视频、未选择的本地文件或 WinDayFlow 数据库。"));
        content.Children.Add(CreateDialogText(
            "关闭此开关会阻止新任务发起网络分析；已经发送的请求仍受所选提供方的条款约束。"));

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "允许向此提供方发送分析证据？",
            Content = content,
            PrimaryButtonText = "允许并启用",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        return await ShowDialogAsync(dialog) == ContentDialogResult.Primary;
    }

    private bool TryReadAiProviderTimeout(out int requestTimeoutSeconds)
    {
        var value = AiProviderTimeoutNumberBox.Value;
        if (!double.IsFinite(value)
            || value != Math.Truncate(value)
            || value is < 10 or > 600)
        {
            requestTimeoutSeconds = 0;
            return false;
        }

        requestTimeoutSeconds = checked((int)value);
        return true;
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

        if (e.PropertyName is nameof(SettingsViewModel.EvidenceRetentionDays)
            or nameof(SettingsViewModel.ExcludeSensitiveApplications)
            or nameof(SettingsViewModel.PauseInRemoteSessions)
            or nameof(SettingsViewModel.PauseDuringScreenSharing))
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

    private void OnAiViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        if (e.PropertyName is nameof(AiProviderSettingsViewModel.DisplayName)
            or nameof(AiProviderSettingsViewModel.BaseEndpoint)
            or nameof(AiProviderSettingsViewModel.Model)
            or nameof(AiProviderSettingsViewModel.RequestTimeoutSeconds))
        {
            SynchronizeAiProviderControls();
        }

        if (e.PropertyName == nameof(AiProviderSettingsViewModel.CloudAnalysisEnabled))
        {
            SynchronizeCloudAnalysisToggle();
        }

        UpdateAiProviderInformation();
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
                    Content = $"{ViewModel.EvidenceRetentionDays} 天",
                    Tag = retentionTag,
                };
                RetentionPicker.Items.Add(matchingItem);
            }

            RetentionPicker.SelectedItem = matchingItem;
            SensitiveApplicationToggle.IsOn = ViewModel.ExcludeSensitiveApplications;
            RemoteSessionToggle.IsOn = ViewModel.PauseInRemoteSessions;
            ScreenSharingToggle.IsOn = ViewModel.PauseDuringScreenSharing;
        }
        finally
        {
            _isUpdatingPrivacyControls = false;
        }
    }

    private void SynchronizeAiProviderControls()
    {
        AiProviderNameTextBox.Text = AiViewModel.DisplayName;
        AiProviderEndpointTextBox.Text = AiViewModel.BaseEndpoint;
        AiProviderModelTextBox.Text = AiViewModel.Model;
        AiProviderTimeoutNumberBox.Value = AiViewModel.RequestTimeoutSeconds;
        ClearAiProviderApiKeyCheckBox.IsChecked = false;
        AiCredentialStatusText.Text = AiViewModel.CredentialStatusText;
        AiValidationStatusText.Text = AiViewModel.ValidationStatusText;
        SynchronizeCloudAnalysisToggle();
    }

    private void SynchronizeCloudAnalysisToggle()
    {
        _isUpdatingCloudAnalysisToggle = true;
        CloudAnalysisToggle.IsOn = AiViewModel.CloudAnalysisEnabled;
        _isUpdatingCloudAnalysisToggle = false;
    }

    private void UpdateAiProviderInformation()
    {
        AiProviderProgressBar.Visibility = AiViewModel.IsBusy
            ? Visibility.Visible
            : Visibility.Collapsed;
        AiCredentialStatusText.Text = AiViewModel.CredentialStatusText;
        AiValidationStatusText.Text = AiViewModel.ValidationStatusText;
        CloudAnalysisStatusText.Text = AiViewModel.CloudAnalysisStatusText;

        if (AiViewModel.HasError)
        {
            AiProviderInfoBar.Title = "分析提供方操作失败";
            AiProviderInfoBar.Message = AiViewModel.ErrorMessage;
            AiProviderInfoBar.Severity = InfoBarSeverity.Error;
        }
        else if (AiViewModel.HasNotice)
        {
            AiProviderInfoBar.Title = "分析提供方已更新";
            AiProviderInfoBar.Message = AiViewModel.NoticeMessage;
            AiProviderInfoBar.Severity = InfoBarSeverity.Success;
        }
        else
        {
            AiProviderInfoBar.Title = AiViewModel.IsValidated
                ? "分析提供方已验证"
                : "分析提供方";
            AiProviderInfoBar.Message = AiViewModel.ValidationStatusText;
            AiProviderInfoBar.Severity = AiViewModel.HasProfile
                ? InfoBarSeverity.Informational
                : InfoBarSeverity.Warning;
        }

        AiProviderInfoBar.IsOpen = true;
    }

    private void ShowAiProviderError(string message)
    {
        AiProviderInfoBar.Title = "请检查分析提供方配置";
        AiProviderInfoBar.Message = message;
        AiProviderInfoBar.Severity = InfoBarSeverity.Error;
        AiProviderInfoBar.IsOpen = true;
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
        UpdateSettingLayout(StorageSettingLayout, DataFolderTextBox, useStackedLayout);
        UpdateSettingLayout(RetentionSettingLayout, RetentionPicker, useStackedLayout);
        UpdateSettingLayout(
            SensitiveApplicationSettingLayout,
            SensitiveApplicationToggle,
            useStackedLayout);
        UpdateSettingLayout(
            ExclusionRulesHeaderLayout,
            AddExclusionRuleButton,
            useStackedLayout);
        UpdateSettingLayout(RemoteSessionSettingLayout, RemoteSessionToggle, useStackedLayout);
        UpdateSettingLayout(ScreenSharingSettingLayout, ScreenSharingToggle, useStackedLayout);
        UpdateSettingLayout(CloudSettingLayout, CloudAnalysisToggle, useStackedLayout);

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
            : control is ComboBox or TextBox
                ? HorizontalAlignment.Stretch
                : HorizontalAlignment.Right;
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
