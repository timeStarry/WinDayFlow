using System.Collections.Specialized;
using System.ComponentModel;
using WinDayFlow.Application.Capture;
using WinDayFlow.Application.Settings;
using WinDayFlow.Presentation.Settings;
using Xunit;

namespace WinDayFlow.Presentation.Tests;

public sealed class SettingsViewModelTests
{
    private static readonly DateTimeOffset ConsentTime =
        new(2026, 7, 16, 5, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(CaptureState.Unavailable, "原生录制组件尚未接入。", false)]
    [InlineData(CaptureState.BlockedByConsent, "需要先查看并同意录制说明。", true)]
    [InlineData(CaptureState.Recording, "正在将屏幕活动记录到本地。", true)]
    [InlineData(CaptureState.Paused, "录制已暂停。", true)]
    [InlineData(CaptureState.Faulted, "录制组件发生错误。", true)]
    [InlineData(CaptureState.Stopped, "录制组件已就绪。", true)]
    public async Task InitialStateProjectsSettingsAndCaptureStatus(
        CaptureState state,
        string expectedAvailabilityText,
        bool expectedBackendAvailable)
    {
        var stored = new AppSettings(
            AppThemePreference.Dark,
            CaptureEnabled: false,
            CloudAnalysisEnabled: true,
            RecordingConsent: null);
        var repository = new TestSettingsRepository(stored);
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        var capture = new TestCaptureService(state);
        using var viewModel = new SettingsViewModel(settings, capture);

        Assert.Equal(AppThemePreference.Dark, viewModel.Theme);
        Assert.False(viewModel.CaptureEnabled);
        Assert.True(viewModel.CloudAnalysisEnabled);
        Assert.False(viewModel.HasValidRecordingConsent);
        Assert.False(viewModel.HasOutdatedRecordingConsent);
        Assert.Equal(CapturePrivacySettings.DefaultRetentionDays, viewModel.EvidenceRetentionDays);
        Assert.True(viewModel.ExcludeSensitiveApplications);
        Assert.True(viewModel.PauseInRemoteSessions);
        Assert.True(viewModel.PauseDuringScreenSharing);
        Assert.Equal(1, viewModel.CapturePrivacyRevision);
        Assert.True(viewModel.CanChangePrivacy);
        Assert.Equal(expectedBackendAvailable, viewModel.IsCaptureBackendAvailable);
        Assert.False(viewModel.CanChangeCapture);
        Assert.True(viewModel.CanGrantConsent);
        Assert.False(viewModel.CanRevokeConsent);
        Assert.Equal("尚未同意屏幕活动录制", viewModel.ConsentStatusText);
        Assert.Equal("录制保持关闭；你仍可使用手工时间线。", viewModel.ConsentDetailText);
        Assert.Equal(expectedAvailabilityText, viewModel.CaptureAvailabilityText);
    }

    [Fact]
    public async Task ThemeAndConsentChangesPersistAndRefreshProjection()
    {
        var repository = new TestSettingsRepository();
        using var settings = new AppSettingsService(
            repository,
            new FixedTimeProvider(ConsentTime));
        await settings.InitializeAsync();
        var capture = new TestCaptureService(CaptureState.Stopped);
        using var viewModel = new SettingsViewModel(settings, capture);
        var changedProperties = ObserveChanges(viewModel);

        Assert.True(await viewModel.SetThemeAsync(AppThemePreference.Dark));
        Assert.True(await viewModel.GrantRecordingConsentAsync());

        Assert.Equal(AppThemePreference.Dark, settings.Current.Theme);
        Assert.Equal(AppThemePreference.Dark, viewModel.Theme);
        var consent = Assert.IsType<RecordingConsent>(
            settings.Current.RecordingConsent);
        Assert.Equal(
            AppSettingsService.CurrentRecordingConsentVersion,
            consent.PolicyVersion);
        Assert.Equal(ConsentTime, consent.AcceptedAtUtc);
        Assert.True(viewModel.HasValidRecordingConsent);
        Assert.True(viewModel.CanChangeCapture);
        Assert.False(viewModel.CanGrantConsent);
        Assert.True(viewModel.CanRevokeConsent);
        Assert.Equal("已同意当前录制说明", viewModel.ConsentStatusText);
        Assert.StartsWith(
            $"版本 {AppSettingsService.CurrentRecordingConsentVersion}",
            viewModel.ConsentDetailText,
            StringComparison.Ordinal);
        Assert.Contains(nameof(SettingsViewModel.Theme), changedProperties);
        Assert.Contains(
            nameof(SettingsViewModel.HasValidRecordingConsent),
            changedProperties);
        Assert.Contains(nameof(SettingsViewModel.CanChangeCapture), changedProperties);
        Assert.Equal(2, repository.SavedSettings.Count);
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public async Task ExplicitExclusionEngineAvailabilityIsProjected()
    {
        var repository = new TestSettingsRepository();
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        var capture = new TestCaptureService(CaptureState.Stopped);
        using var viewModel = new SettingsViewModel(
            settings,
            capture,
            isExclusionEngineAvailable: true);

        Assert.True(viewModel.IsExclusionEngineAvailable);
        Assert.Contains("监视器已就绪", viewModel.ExclusionEngineStatusText);
    }

    [Fact]
    public async Task PrivacyChangePersistsFailClosedStateBeforeStoppingCapture()
    {
        var consent = CreateConsent();
        var repository = new TestSettingsRepository(
            new AppSettings(
                AppThemePreference.System,
                CaptureEnabled: true,
                CloudAnalysisEnabled: false,
                consent));
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        var capture = new TestCaptureService(CaptureState.Recording)
        {
            StopOperation = _ =>
            {
                Assert.False(settings.Current.CaptureEnabled);
                Assert.False(settings.HasValidRecordingConsent);
                Assert.Equal(90, settings.Current.CapturePrivacy.EvidenceRetentionDays);
                return Task.CompletedTask;
            },
        };
        using var viewModel = new SettingsViewModel(settings, capture);

        Assert.True(await viewModel.SetCapturePrivacyAsync(
            evidenceRetentionDays: 90,
            excludeSensitiveApplications: false,
            pauseInRemoteSessions: true,
            pauseDuringScreenSharing: false));

        Assert.Equal(1, capture.StopCount);
        Assert.Equal(CaptureState.Stopped, capture.CurrentStatus.State);
        Assert.False(settings.Current.CaptureEnabled);
        Assert.False(settings.HasValidRecordingConsent);
        Assert.True(viewModel.HasOutdatedRecordingConsent);
        Assert.Equal(90, viewModel.EvidenceRetentionDays);
        Assert.False(viewModel.ExcludeSensitiveApplications);
        Assert.False(viewModel.PauseDuringScreenSharing);
        Assert.Equal(2, viewModel.CapturePrivacyRevision);
        Assert.Equal("录制说明或隐私选择已更新", viewModel.ConsentStatusText);
        Assert.Single(repository.SavedSettings);
    }

    [Fact]
    public async Task PrivacyChangeStopFailureStillPersistsFailClosedSettings()
    {
        var consent = CreateConsent();
        var initial = new AppSettings(
            AppThemePreference.System,
            CaptureEnabled: true,
            CloudAnalysisEnabled: false,
            consent);
        var repository = new TestSettingsRepository(initial);
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        var capture = new TestCaptureService(CaptureState.Recording)
        {
            StopOperation = _ => throw new InvalidOperationException("Sensitive detail."),
        };
        using var viewModel = new SettingsViewModel(settings, capture);

        Assert.False(await viewModel.SetCapturePrivacyAsync(
            evidenceRetentionDays: 7,
            excludeSensitiveApplications: false,
            pauseInRemoteSessions: false,
            pauseDuringScreenSharing: false));

        Assert.Equal(1, capture.StopCount);
        Assert.False(settings.Current.CaptureEnabled);
        Assert.False(settings.HasValidRecordingConsent);
        Assert.Equal(7, settings.Current.CapturePrivacy.EvidenceRetentionDays);
        Assert.False(settings.Current.CapturePrivacy.ExcludeSensitiveApplications);
        Assert.Single(repository.SavedSettings);
        Assert.Equal("无法更改录制状态，请稍后重试。", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task PrivacyChangeSaveFailureLeavesRuntimeAndSettingsConsistent()
    {
        var consent = CreateConsent();
        var initial = new AppSettings(
            AppThemePreference.System,
            CaptureEnabled: true,
            CloudAnalysisEnabled: false,
            consent);
        var repository = new TestSettingsRepository(initial)
        {
            SaveException = new InvalidOperationException("Sensitive storage detail."),
        };
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        var capture = new TestCaptureService(CaptureState.Recording);
        using var viewModel = new SettingsViewModel(settings, capture);

        Assert.False(await viewModel.SetCapturePrivacyAsync(
            evidenceRetentionDays: 7,
            excludeSensitiveApplications: false,
            pauseInRemoteSessions: false,
            pauseDuringScreenSharing: false));

        Assert.Equal(0, capture.StopCount);
        Assert.Equal(CaptureState.Recording, capture.CurrentStatus.State);
        Assert.Equal(initial, settings.Current);
        Assert.True(settings.HasValidRecordingConsent);
        Assert.Empty(repository.SavedSettings);
        Assert.Equal("无法更改录制状态，请稍后重试。", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task ExclusionRuleCrudRefreshesOrderedProjectionAndNotices()
    {
        var repository = new TestSettingsRepository();
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        var capture = new TestCaptureService(CaptureState.Stopped);
        using var viewModel = new SettingsViewModel(settings, capture);

        Assert.Empty(viewModel.ExclusionRules);
        Assert.False(viewModel.HasExclusionRules);
        Assert.False(viewModel.IsExclusionEngineAvailable);
        Assert.Contains("尚未接入录制监视器", viewModel.ExclusionEngineStatusText);

        Assert.True(await viewModel.AddExclusionRuleAsync(
            "密码管理器",
            enabled: true,
            CaptureExclusionRuleScope.Application,
            ApplicationIdentityKind.ExecutableName,
            "KeePassXC.exe",
            windowTitleMatchKind: null,
            pattern: null));
        var applicationRule = Assert.Single(viewModel.ExclusionRules);
        Assert.Equal("密码管理器", applicationRule.Name);
        Assert.Equal("规则已启用", applicationRule.StatusText);
        Assert.Contains("KeePassXC.exe", applicationRule.ConfiguredMatchSummaryText);
        Assert.Equal("排除规则已添加。", viewModel.RuleMutationNoticeText);

        Assert.True(await viewModel.AddExclusionRuleAsync(
            "私密浏览",
            enabled: false,
            CaptureExclusionRuleScope.Window,
            ApplicationIdentityKind.ExecutableName,
            "browser.exe",
            WindowTitleMatchKind.Contains,
            "Private"));
        var windowRule = viewModel.ExclusionRules[1];
        Assert.Equal("2 条规则 · 1 条已启用", viewModel.ExclusionRuleSummaryText);
        Assert.NotEqual(applicationRule.ToggleAutomationId, windowRule.ToggleAutomationId);
        Assert.NotEqual(applicationRule.EditAutomationId, windowRule.EditAutomationId);
        Assert.True(windowRule.CanMoveUp);
        Assert.False(windowRule.CanMoveDown);

        Assert.True(await viewModel.UpdateExclusionRuleAsync(
            windowRule,
            "浏览器私密窗口",
            CaptureExclusionRuleScope.Window,
            ApplicationIdentityKind.ExecutableName,
            "browser.exe",
            WindowTitleMatchKind.StartsWith,
            "Private"));
        Assert.Equal("浏览器私密窗口", windowRule.Name);
        Assert.Contains("开头匹配", windowRule.ConfiguredMatchSummaryText);
        Assert.Equal("排除规则已保存。", viewModel.RuleMutationNoticeText);

        Assert.True(await viewModel.SetExclusionRuleEnabledAsync(windowRule, enabled: true));
        Assert.True(windowRule.IsEnabled);
        Assert.Equal("2 条规则 · 2 条已启用", viewModel.ExclusionRuleSummaryText);

        Assert.True(await viewModel.MoveExclusionRuleAsync(windowRule, offset: -1));
        Assert.Same(windowRule, viewModel.ExclusionRules[0]);
        Assert.False(windowRule.CanMoveUp);
        Assert.True(windowRule.CanMoveDown);

        Assert.True(await viewModel.DeleteExclusionRuleAsync(applicationRule));
        Assert.Same(windowRule, Assert.Single(viewModel.ExclusionRules));
        Assert.Equal("排除规则已删除。", viewModel.RuleMutationNoticeText);
        viewModel.ClearRuleMutationNotice();
        Assert.False(viewModel.HasRuleMutationNotice);
    }

    [Fact]
    public async Task EffectiveExclusionRuleChangePersistsClosedStateBeforeStoppingCapture()
    {
        var repository = new TestSettingsRepository(
            new AppSettings(
                AppThemePreference.System,
                CaptureEnabled: true,
                CloudAnalysisEnabled: false,
                CreateConsent()));
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        var capture = new TestCaptureService(CaptureState.Recording)
        {
            StopOperation = _ =>
            {
                Assert.False(settings.Current.CaptureEnabled);
                Assert.False(settings.HasValidRecordingConsent);
                Assert.Single(settings.Current.CapturePrivacy.ExclusionRules.Rules);
                return Task.CompletedTask;
            },
        };
        using var viewModel = new SettingsViewModel(settings, capture);

        Assert.True(await viewModel.AddExclusionRuleAsync(
            "密码管理器",
            enabled: true,
            CaptureExclusionRuleScope.Application,
            ApplicationIdentityKind.ExecutableName,
            "KeePassXC.exe",
            windowTitleMatchKind: null,
            pattern: null));

        Assert.Equal(1, capture.StopCount);
        Assert.Equal(CaptureState.Stopped, capture.CurrentStatus.State);
        Assert.False(viewModel.CaptureEnabled);
        Assert.Equal(2, viewModel.CapturePrivacyRevision);
    }

    [Fact]
    public async Task DisabledExclusionRuleDraftDoesNotStopCaptureOrInvalidateConsent()
    {
        var repository = new TestSettingsRepository(
            new AppSettings(
                AppThemePreference.System,
                CaptureEnabled: true,
                CloudAnalysisEnabled: false,
                CreateConsent()));
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        var capture = new TestCaptureService(CaptureState.Recording);
        using var viewModel = new SettingsViewModel(settings, capture);

        Assert.True(await viewModel.AddExclusionRuleAsync(
            "稍后启用",
            enabled: false,
            CaptureExclusionRuleScope.Application,
            ApplicationIdentityKind.ExecutableName,
            "draft.exe",
            windowTitleMatchKind: null,
            pattern: null));

        Assert.Equal(0, capture.StopCount);
        Assert.True(viewModel.CaptureEnabled);
        Assert.True(viewModel.HasValidRecordingConsent);
        Assert.Equal(1, viewModel.CapturePrivacyRevision);
        Assert.False(Assert.Single(viewModel.ExclusionRules).IsEnabled);
    }

    [Fact]
    public async Task ExclusionRuleSaveFailureLeavesProjectionUnchanged()
    {
        var repository = new TestSettingsRepository
        {
            SaveException = new InvalidOperationException("Sensitive storage detail."),
        };
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        var capture = new TestCaptureService(CaptureState.Stopped);
        using var viewModel = new SettingsViewModel(settings, capture);

        Assert.False(await viewModel.AddExclusionRuleAsync(
            "密码管理器",
            enabled: true,
            CaptureExclusionRuleScope.Application,
            ApplicationIdentityKind.ExecutableName,
            "KeePassXC.exe",
            windowTitleMatchKind: null,
            pattern: null));

        Assert.Empty(viewModel.ExclusionRules);
        Assert.False(viewModel.HasExclusionRules);
        Assert.False(viewModel.HasRuleMutationNotice);
        Assert.Equal("无法更改排除规则，请稍后重试。", viewModel.ErrorMessage);
        Assert.Empty(repository.SavedSettings);
    }

    [Fact]
    public async Task GrantFailureUsesStableErrorAndCanBeCleared()
    {
        var repository = new TestSettingsRepository
        {
            SaveException = new InvalidOperationException("Sensitive storage detail."),
        };
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        var capture = new TestCaptureService(CaptureState.Stopped);
        using var viewModel = new SettingsViewModel(settings, capture);

        Assert.False(await viewModel.GrantRecordingConsentAsync());

        Assert.False(settings.HasValidRecordingConsent);
        Assert.True(viewModel.HasError);
        Assert.Equal("无法保存设置，请稍后重试。", viewModel.ErrorMessage);

        viewModel.ClearError();

        Assert.False(viewModel.HasError);
        Assert.Empty(viewModel.ErrorMessage);
    }

    [Fact]
    public async Task RevokePersistsFailClosedStateBeforeStoppingCapture()
    {
        var consent = CreateConsent();
        var repository = new TestSettingsRepository(
            new AppSettings(
                AppThemePreference.System,
                CaptureEnabled: true,
                CloudAnalysisEnabled: false,
                consent));
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        var capture = new TestCaptureService(CaptureState.Recording)
        {
            StopOperation = _ =>
            {
                Assert.False(settings.Current.CaptureEnabled);
                Assert.Null(settings.Current.RecordingConsent);
                return Task.CompletedTask;
            },
        };
        using var viewModel = new SettingsViewModel(settings, capture);

        Assert.True(await viewModel.RevokeRecordingConsentAsync());

        Assert.Equal(1, capture.StopCount);
        Assert.False(settings.Current.CaptureEnabled);
        Assert.Null(settings.Current.RecordingConsent);
        Assert.False(viewModel.HasValidRecordingConsent);
        Assert.False(viewModel.CaptureEnabled);
        Assert.True(viewModel.CanGrantConsent);
        Assert.False(viewModel.CanRevokeConsent);
        Assert.Equal(CaptureState.Stopped, capture.CurrentStatus.State);
        Assert.Single(repository.SavedSettings);
    }

    [Fact]
    public async Task RevokeStopFailureStillClearsConsentAndReportsCaptureError()
    {
        var consent = CreateConsent();
        var repository = new TestSettingsRepository(
            new AppSettings(
                AppThemePreference.System,
                CaptureEnabled: true,
                CloudAnalysisEnabled: false,
                consent));
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        var capture = new TestCaptureService(CaptureState.Recording)
        {
            StopOperation = _ => throw new InvalidOperationException(
                "Sensitive capture detail."),
        };
        using var viewModel = new SettingsViewModel(settings, capture);

        Assert.False(await viewModel.RevokeRecordingConsentAsync());

        Assert.Equal(1, capture.StopCount);
        Assert.False(settings.Current.CaptureEnabled);
        Assert.Null(settings.Current.RecordingConsent);
        Assert.False(settings.HasValidRecordingConsent);
        Assert.Single(repository.SavedSettings);
        Assert.Equal("无法更改录制状态，请稍后重试。", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task RevokeSaveFailureLeavesRuntimeAndSettingsConsistent()
    {
        var consent = CreateConsent();
        var initial = new AppSettings(
            AppThemePreference.System,
            CaptureEnabled: true,
            CloudAnalysisEnabled: false,
            consent);
        var repository = new TestSettingsRepository(initial)
        {
            SaveException = new InvalidOperationException("Sensitive storage detail."),
        };
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        var capture = new TestCaptureService(CaptureState.Recording);
        using var viewModel = new SettingsViewModel(settings, capture);

        Assert.False(await viewModel.RevokeRecordingConsentAsync());

        Assert.Equal(0, capture.StopCount);
        Assert.Equal(CaptureState.Recording, capture.CurrentStatus.State);
        Assert.Equal(initial, settings.Current);
        Assert.True(settings.HasValidRecordingConsent);
        Assert.Empty(repository.SavedSettings);
        Assert.Equal("无法更改录制状态，请稍后重试。", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task CaptureEnableAndDisableUseSafeOperationOrder()
    {
        var repository = new TestSettingsRepository(
            new AppSettings(
                AppThemePreference.System,
                CaptureEnabled: false,
                CloudAnalysisEnabled: false,
                CreateConsent()));
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        var capture = new TestCaptureService(CaptureState.Stopped)
        {
            StartOperation = _ =>
            {
                Assert.True(settings.Current.CaptureEnabled);
                return Task.CompletedTask;
            },
            StopOperation = _ =>
            {
                Assert.True(settings.Current.CaptureEnabled);
                return Task.CompletedTask;
            },
        };
        using var viewModel = new SettingsViewModel(settings, capture);

        Assert.True(await viewModel.SetCaptureEnabledAsync(enabled: true));
        Assert.True(settings.Current.CaptureEnabled);
        Assert.Equal(CaptureState.Recording, capture.CurrentStatus.State);

        Assert.True(await viewModel.SetCaptureEnabledAsync(enabled: false));
        Assert.False(settings.Current.CaptureEnabled);
        Assert.Equal(CaptureState.Stopped, capture.CurrentStatus.State);
        Assert.Equal(1, capture.StartCount);
        Assert.Equal(1, capture.StopCount);
        Assert.Equal(2, repository.SavedSettings.Count);
    }

    [Fact]
    public async Task CaptureStopFailureStillPersistsDisabledState()
    {
        var initial = new AppSettings(
            AppThemePreference.System,
            CaptureEnabled: true,
            CloudAnalysisEnabled: false,
            CreateConsent());
        var repository = new TestSettingsRepository(initial);
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        var stopFailure = new InvalidOperationException("Sensitive capture detail.");
        var capture = new TestCaptureService(CaptureState.Recording)
        {
            StopOperation = _ =>
            {
                Assert.True(settings.Current.CaptureEnabled);
                throw stopFailure;
            },
        };
        using var viewModel = new SettingsViewModel(settings, capture);

        Assert.False(await viewModel.SetCaptureEnabledAsync(enabled: false));

        Assert.Equal(1, capture.StopCount);
        Assert.Equal(CaptureState.Recording, capture.CurrentStatus.State);
        Assert.False(settings.Current.CaptureEnabled);
        Assert.False(viewModel.CaptureEnabled);
        Assert.Collection(
            repository.SavedSettings,
            saved => Assert.False(saved.CaptureEnabled));
        Assert.Equal(1, repository.SaveAttemptCount);
        Assert.Equal("无法更改录制状态，请稍后重试。", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task CaptureDisableWaitsForDurableStopBeforePersistingState()
    {
        var initial = new AppSettings(
            AppThemePreference.System,
            CaptureEnabled: true,
            CloudAnalysisEnabled: false,
            CreateConsent());
        var repository = new TestSettingsRepository(initial);
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        var stopEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStop = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var capture = new TestCaptureService(CaptureState.Recording)
        {
            StopOperation = async _ =>
            {
                stopEntered.TrySetResult();
                await releaseStop.Task;
            },
        };
        using var viewModel = new SettingsViewModel(settings, capture);

        var mutation = viewModel.SetCaptureEnabledAsync(enabled: false);
        await stopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(settings.Current.CaptureEnabled);
        Assert.Equal(0, repository.SaveAttemptCount);
        Assert.False(mutation.IsCompleted);

        releaseStop.TrySetResult();
        Assert.True(await mutation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(settings.Current.CaptureEnabled);
        Assert.Equal(1, repository.SaveAttemptCount);
    }

    [Fact]
    public async Task CaptureStopCancellationPersistsDisabledStateBeforePropagating()
    {
        var initial = new AppSettings(
            AppThemePreference.System,
            CaptureEnabled: true,
            CloudAnalysisEnabled: false,
            CreateConsent());
        var repository = new TestSettingsRepository(initial);
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        var stopEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var capture = new TestCaptureService(CaptureState.Recording)
        {
            StopOperation = async token =>
            {
                Assert.True(settings.Current.CaptureEnabled);
                stopEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
        };
        using var viewModel = new SettingsViewModel(settings, capture);
        using var cancellation = new CancellationTokenSource();

        var mutation = viewModel.SetCaptureEnabledAsync(
            enabled: false,
            cancellation.Token);
        await stopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mutation);
        Assert.Equal(1, capture.StopCount);
        Assert.False(settings.Current.CaptureEnabled);
        Assert.False(viewModel.CaptureEnabled);
        Assert.Collection(
            repository.SavedSettings,
            saved => Assert.False(saved.CaptureEnabled));
        Assert.Equal(1, repository.SaveAttemptCount);
        Assert.False(viewModel.IsBusy);
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public async Task CaptureStopAndDisablePersistenceFailuresAttemptBothOperations()
    {
        var initial = new AppSettings(
            AppThemePreference.System,
            CaptureEnabled: true,
            CloudAnalysisEnabled: false,
            CreateConsent());
        var persistenceFailure = new InvalidOperationException("Sensitive storage detail.");
        var repository = new TestSettingsRepository(initial)
        {
            SaveException = persistenceFailure,
        };
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        var stopFailure = new InvalidOperationException("Sensitive capture detail.");
        var capture = new TestCaptureService(CaptureState.Recording)
        {
            StopOperation = _ => throw stopFailure,
        };
        using var viewModel = new SettingsViewModel(settings, capture);

        Assert.False(await viewModel.SetCaptureEnabledAsync(enabled: false));

        Assert.Equal(1, capture.StopCount);
        Assert.Equal(1, repository.SaveAttemptCount);
        Assert.Empty(repository.SavedSettings);
        Assert.True(settings.Current.CaptureEnabled);
        Assert.Equal("无法更改录制状态，请稍后重试。", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task CaptureStartFailureRollsBackPersistedEnablement()
    {
        var repository = new TestSettingsRepository(
            new AppSettings(
                AppThemePreference.System,
                CaptureEnabled: false,
                CloudAnalysisEnabled: false,
                CreateConsent()));
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        var capture = new TestCaptureService(CaptureState.Stopped)
        {
            StartOperation = _ =>
            {
                Assert.True(settings.Current.CaptureEnabled);
                throw new InvalidOperationException("Sensitive capture detail.");
            },
        };
        using var viewModel = new SettingsViewModel(settings, capture);

        Assert.False(await viewModel.SetCaptureEnabledAsync(enabled: true));

        Assert.Equal(1, capture.StartCount);
        Assert.False(settings.Current.CaptureEnabled);
        Assert.False(viewModel.CaptureEnabled);
        Assert.Collection(
            repository.SavedSettings,
            saved => Assert.True(saved.CaptureEnabled),
            saved => Assert.False(saved.CaptureEnabled));
        Assert.Equal("无法更改录制状态，请稍后重试。", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task CaptureStartCancellationRollsBackBeforePropagating()
    {
        var repository = new TestSettingsRepository(
            new AppSettings(
                AppThemePreference.System,
                CaptureEnabled: false,
                CloudAnalysisEnabled: false,
                CreateConsent()));
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        var startEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var capture = new TestCaptureService(CaptureState.Stopped)
        {
            StartOperation = async token =>
            {
                startEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
        };
        using var viewModel = new SettingsViewModel(settings, capture);
        using var cancellation = new CancellationTokenSource();

        var mutation = viewModel.SetCaptureEnabledAsync(
            enabled: true,
            cancellation.Token);
        await startEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mutation);
        Assert.False(settings.Current.CaptureEnabled);
        Assert.False(viewModel.IsBusy);
        Assert.False(viewModel.HasError);
        Assert.Collection(
            repository.SavedSettings,
            saved => Assert.True(saved.CaptureEnabled),
            saved => Assert.False(saved.CaptureEnabled));
    }

    [Fact]
    public async Task EnabledCaptureCanBeDisabledWhenBackendBecomesUnavailable()
    {
        var repository = new TestSettingsRepository(
            new AppSettings(
                AppThemePreference.System,
                CaptureEnabled: true,
                CloudAnalysisEnabled: false,
                CreateConsent()));
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        var capture = new TestCaptureService(CaptureState.Unavailable);
        using var viewModel = new SettingsViewModel(settings, capture);

        Assert.True(viewModel.CanChangeCapture);
        Assert.True(await viewModel.SetCaptureEnabledAsync(enabled: false));

        Assert.False(viewModel.CaptureEnabled);
        Assert.False(viewModel.CanChangeCapture);
        Assert.Equal(0, capture.StopCount);
        Assert.Single(repository.SavedSettings);
    }

    [Fact]
    public async Task ConcurrentMutationIsRejectedWithoutResettingBusyState()
    {
        var repository = new TestSettingsRepository
        {
            BlockFirstSave = true,
        };
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        var capture = new TestCaptureService(CaptureState.Stopped);
        using var viewModel = new SettingsViewModel(settings, capture);

        var first = viewModel.SetThemeAsync(AppThemePreference.Dark);
        await repository.FirstSaveStarted.WaitAsync(TimeSpan.FromSeconds(5));

        var second = await viewModel.GrantRecordingConsentAsync();

        Assert.False(second);
        Assert.True(viewModel.IsBusy);
        Assert.Equal("另一项设置操作正在进行，请稍候。", viewModel.ErrorMessage);

        repository.ReleaseFirstSave();
        Assert.True(await first);
        Assert.False(viewModel.IsBusy);
        Assert.Equal(AppThemePreference.Dark, viewModel.Theme);
        Assert.False(viewModel.HasValidRecordingConsent);
        Assert.Single(repository.SavedSettings);
    }

    [Fact]
    public async Task CallerCancellationPropagatesAndResetsBusyState()
    {
        var repository = new TestSettingsRepository
        {
            WaitForFirstSaveCancellation = true,
        };
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        var capture = new TestCaptureService(CaptureState.Stopped);
        using var viewModel = new SettingsViewModel(settings, capture);
        using var cancellation = new CancellationTokenSource();

        var mutation = viewModel.SetThemeAsync(
            AppThemePreference.Dark,
            cancellation.Token);
        await repository.FirstSaveStarted.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mutation);
        Assert.False(viewModel.IsBusy);
        Assert.Equal(AppThemePreference.System, viewModel.Theme);
        Assert.False(viewModel.HasError);
        Assert.Empty(repository.SavedSettings);
    }

    [Fact]
    public async Task DisposeCancelsMutationDetachesEventsAndRejectsFurtherWrites()
    {
        var repository = new TestSettingsRepository
        {
            WaitForFirstSaveCancellation = true,
        };
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        var capture = new TestCaptureService(CaptureState.Stopped);
        var viewModel = new SettingsViewModel(settings, capture);
        var changedProperties = ObserveChanges(viewModel);

        var mutation = viewModel.SetThemeAsync(AppThemePreference.Dark);
        await repository.FirstSaveStarted.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, capture.SubscriptionCount);

        viewModel.Dispose();

        Assert.False(await mutation);
        Assert.False(viewModel.IsBusy);
        Assert.Equal(0, capture.SubscriptionCount);
        changedProperties.Clear();

        Assert.False(await viewModel.GrantRecordingConsentAsync());
        await settings.SetThemeAsync(AppThemePreference.Light);
        capture.TransitionTo(CaptureState.Unavailable);

        Assert.Empty(changedProperties);
        Assert.Equal(AppThemePreference.Light, viewModel.Theme);
    }

    [Fact]
    public async Task DisposeCancelsInFlightExclusionRuleWithoutPublishingProjectionOrNotice()
    {
        var repository = new TestSettingsRepository
        {
            WaitForFirstSaveCancellation = true,
        };
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        var capture = new TestCaptureService(CaptureState.Stopped);
        var viewModel = new SettingsViewModel(settings, capture);
        var collectionChanges = 0;
        ((INotifyCollectionChanged)viewModel.ExclusionRules).CollectionChanged +=
            (_, _) => collectionChanges++;

        var mutation = viewModel.AddExclusionRuleAsync(
            "密码管理器",
            enabled: true,
            CaptureExclusionRuleScope.Application,
            ApplicationIdentityKind.ExecutableName,
            "KeePassXC.exe",
            windowTitleMatchKind: null,
            pattern: null);
        await repository.FirstSaveStarted.WaitAsync(TimeSpan.FromSeconds(5));

        viewModel.Dispose();

        Assert.False(await mutation);
        Assert.Empty(viewModel.ExclusionRules);
        Assert.False(viewModel.HasRuleMutationNotice);
        Assert.Equal(0, collectionChanges);
        Assert.Empty(repository.SavedSettings);
    }

    [Fact]
    public async Task DisposedExclusionRuleMutationPreservesProjectionAndExistingNotice()
    {
        var repository = new TestSettingsRepository();
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        var capture = new TestCaptureService(CaptureState.Stopped);
        var viewModel = new SettingsViewModel(settings, capture);

        Assert.True(await viewModel.AddExclusionRuleAsync(
            "稍后启用",
            enabled: false,
            CaptureExclusionRuleScope.Application,
            ApplicationIdentityKind.ExecutableName,
            "draft.exe",
            windowTitleMatchKind: null,
            pattern: null));
        var item = Assert.Single(viewModel.ExclusionRules);
        var notice = viewModel.RuleMutationNoticeText;
        var changedProperties = ObserveChanges(viewModel);
        var collectionChanges = 0;
        ((INotifyCollectionChanged)viewModel.ExclusionRules).CollectionChanged +=
            (_, _) => collectionChanges++;

        viewModel.Dispose();
        changedProperties.Clear();

        Assert.False(await viewModel.DeleteExclusionRuleAsync(item));
        Assert.Same(item, Assert.Single(viewModel.ExclusionRules));
        Assert.Equal(notice, viewModel.RuleMutationNoticeText);
        Assert.Empty(changedProperties);
        Assert.Equal(0, collectionChanges);
        Assert.Single(repository.SavedSettings);
    }

    [Fact]
    public async Task DisposeDropsCaptureUpdateAlreadyQueuedForUiDispatch()
    {
        var repository = new TestSettingsRepository();
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        var capture = new TestCaptureService(CaptureState.Stopped);
        var dispatchContext = new QueuedSynchronizationContext();
        var previousContext = SynchronizationContext.Current;
        SettingsViewModel viewModel;
        try
        {
            SynchronizationContext.SetSynchronizationContext(dispatchContext);
            viewModel = new SettingsViewModel(settings, capture);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        var changedProperties = ObserveChanges(viewModel);
        await Task.Run(() => capture.TransitionTo(CaptureState.Unavailable));
        await dispatchContext.Posted.WaitAsync(TimeSpan.FromSeconds(5));

        viewModel.Dispose();
        dispatchContext.RunPostedCallback();

        Assert.Empty(changedProperties);
        Assert.Equal("原生录制组件尚未接入。", viewModel.CaptureAvailabilityText);
    }

    private static RecordingConsent CreateConsent()
    {
        return new RecordingConsent(
            AppSettingsService.CurrentRecordingConsentVersion,
            ConsentTime,
            CapturePrivacySettings.Default.Revision);
    }

    private static HashSet<string> ObserveChanges(INotifyPropertyChanged source)
    {
        var properties = new HashSet<string>(StringComparer.Ordinal);
        source.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is not null)
            {
                properties.Add(args.PropertyName);
            }
        };
        return properties;
    }

    private sealed class TestSettingsRepository : IAppSettingsRepository
    {
        private readonly TaskCompletionSource _firstSaveStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstSave =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private AppSettings _settings;
        private int _saveCallCount;

        public TestSettingsRepository(AppSettings? settings = null)
        {
            _settings = settings ?? AppSettings.Default;
        }

        public bool BlockFirstSave { get; init; }

        public bool WaitForFirstSaveCancellation { get; init; }

        public Exception? SaveException { get; init; }

        public Task FirstSaveStarted => _firstSaveStarted.Task;

        public List<AppSettings> SavedSettings { get; } = [];

        public int SaveAttemptCount => Volatile.Read(ref _saveCallCount);

        public Task<AppSettings> GetAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_settings);
        }

        public async Task SaveAsync(
            AppSettings expected,
            AppSettings proposed,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = Interlocked.Increment(ref _saveCallCount);
            if (call == 1)
            {
                _firstSaveStarted.TrySetResult();
                if (WaitForFirstSaveCancellation)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }

                if (BlockFirstSave)
                {
                    await _releaseFirstSave.Task.WaitAsync(cancellationToken);
                }
            }

            if (SaveException is not null)
            {
                throw SaveException;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (_settings != expected)
            {
                throw new AppSettingsConcurrencyException();
            }

            _settings = proposed;
            SavedSettings.Add(proposed);
        }

        public void ReleaseFirstSave()
        {
            _releaseFirstSave.TrySetResult();
        }
    }

    private sealed class TestCaptureService : ICaptureService
    {
        private static readonly DateTimeOffset StatusTime =
            new(2026, 7, 16, 6, 0, 0, TimeSpan.Zero);
        private EventHandler<CaptureStatusChangedEventArgs>? _statusChanged;

        public TestCaptureService(CaptureState initialState)
        {
            CurrentStatus = CreateStatus(initialState);
        }

        public CaptureStatus CurrentStatus { get; private set; }

        public Func<CancellationToken, Task>? StartOperation { get; init; }

        public Func<CancellationToken, Task>? StopOperation { get; init; }

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public int SubscriptionCount { get; private set; }

        public event EventHandler<CaptureStatusChangedEventArgs>? StatusChanged
        {
            add
            {
                _statusChanged += value;
                SubscriptionCount++;
            }
            remove
            {
                _statusChanged -= value;
                SubscriptionCount--;
            }
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            if (StartOperation is not null)
            {
                await StartOperation(cancellationToken);
            }

            TransitionTo(CaptureState.Recording);
        }

        public Task PauseAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TransitionTo(CaptureState.Paused);
            return Task.CompletedTask;
        }

        public Task ResumeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TransitionTo(CaptureState.Recording);
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCount++;
            if (StopOperation is not null)
            {
                await StopOperation(cancellationToken);
            }

            TransitionTo(CaptureState.Stopped);
        }

        public void TransitionTo(CaptureState state)
        {
            var previous = CurrentStatus;
            CurrentStatus = CreateStatus(state);
            _statusChanged?.Invoke(
                this,
                new CaptureStatusChangedEventArgs(previous, CurrentStatus));
        }

        private static CaptureStatus CreateStatus(CaptureState state)
        {
            return new CaptureStatus(
                state,
                StatusTime,
                Reason: state switch
                {
                    CaptureState.Unavailable => CaptureReasonCode.BackendUnavailable,
                    CaptureState.Faulted => CaptureReasonCode.BackendFault,
                    _ => CaptureReasonCode.None,
                },
                ErrorCode: state == CaptureState.Faulted
                    ? CaptureErrorCode.Unknown
                    : CaptureErrorCode.None);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly TaskCompletionSource _posted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private SendOrPostCallback? _callback;
        private object? _state;

        public Task Posted => _posted.Task;

        public override void Post(SendOrPostCallback callback, object? state)
        {
            _callback = callback;
            _state = state;
            _posted.TrySetResult();
        }

        public void RunPostedCallback()
        {
            Assert.NotNull(_callback);
            _callback(_state);
        }
    }
}
