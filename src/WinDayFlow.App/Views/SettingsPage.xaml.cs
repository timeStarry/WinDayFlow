using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinDayFlow.Application.Settings;
using WinDayFlow.Presentation.Settings;

namespace WinDayFlow.App.Views;

public sealed partial class SettingsPage : Page
{
    private const double StackedLayoutMaximumWidth = 620;

    private bool _dialogOpen;
    private bool _isSubscribed;
    private bool _isUpdatingCaptureToggle;
    private bool _isUpdatingPrivacyControls;
    private bool _isUpdatingThemePicker;

    public SettingsPage()
    {
        ViewModel = App.GetService<SettingsViewModel>();
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
    }

    public SettingsViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_isSubscribed)
        {
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            _isSubscribed = true;
        }

        UpdateResponsiveLayout(ActualWidth);
        SynchronizeThemePicker();
        SynchronizeCaptureToggle();
        SynchronizePrivacyControls();
        UpdateConsentActions();
        UpdateCaptureInformation();
        UpdateErrorInformation();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_isSubscribed)
        {
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _isSubscribed = false;
        }

        SizeChanged -= OnSizeChanged;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        ViewModel.Dispose();
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
            "外部传输：云分析提供方尚未接入。未来启用云端分析前会提供独立开关和说明。"));
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

        if (e.PropertyName == nameof(SettingsViewModel.IsBusy))
        {
            ThemePicker.IsEnabled = !ViewModel.IsBusy;
        }
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
}
