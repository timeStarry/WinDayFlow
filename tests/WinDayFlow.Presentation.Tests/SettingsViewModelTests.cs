using WinDayFlow.Application.Capture;
using WinDayFlow.Application.Settings;
using WinDayFlow.Presentation.Settings;
using Xunit;

namespace WinDayFlow.Presentation.Tests;

public sealed class SettingsViewModelTests
{
    [Theory]
    [InlineData(CaptureState.Unavailable, "原生录制组件尚未接入。", false)]
    [InlineData(CaptureState.BlockedByConsent, "需要先查看并同意录制说明。", true)]
    [InlineData(CaptureState.Recording, "正在将屏幕活动记录到本地。", true)]
    [InlineData(CaptureState.Paused, "录制已由用户暂停。", true)]
    [InlineData(CaptureState.Stopped, "录制组件已就绪。", true)]
    public async Task InitialStateProjectsV13Settings(
        CaptureState state,
        string expectedText,
        bool backendAvailable)
    {
        using var settings = await CreateSettingsAsync();
        var capture = new TestCaptureService(settings, state);
        using var viewModel = new SettingsViewModel(settings, capture);

        Assert.Equal(AppThemePreference.System, viewModel.Theme);
        Assert.False(viewModel.CaptureEnabled);
        Assert.False(viewModel.HasValidRecordingConsent);
        Assert.Equal(10, viewModel.CaptureIntervalSeconds);
        Assert.Equal(EvidenceSettings.DefaultRetentionDays, viewModel.EvidenceRetentionDays);
        Assert.Equal(1, viewModel.ExclusionRuleCount);
        Assert.Equal(1, viewModel.EnabledExclusionRuleCount);
        Assert.Equal(backendAvailable, viewModel.IsCaptureBackendAvailable);
        Assert.Equal(expectedText, viewModel.CaptureAvailabilityText);
        Assert.Contains("不会暂停或停止本地录制", viewModel.ExclusionEngineStatusText);
    }

    [Fact]
    public async Task CaptureToggleDelegatesToCaptureServiceAndPersistsIntent()
    {
        using var settings = await CreateSettingsAsync();
        await settings.GrantRecordingConsentAsync();
        var capture = new TestCaptureService(settings, CaptureState.Stopped);
        using var viewModel = new SettingsViewModel(settings, capture);

        Assert.True(await viewModel.SetCaptureEnabledAsync(enabled: true));
        Assert.Equal(1, capture.StartCalls);
        Assert.Equal(CaptureIntent.Recording, settings.Current.CaptureIntent);
        Assert.True(viewModel.CaptureEnabled);

        Assert.True(await viewModel.SetCaptureEnabledAsync(enabled: false));
        Assert.Equal(1, capture.StopCalls);
        Assert.Equal(CaptureIntent.Stopped, settings.Current.CaptureIntent);
    }

    [Fact]
    public async Task ChangingIntervalRestartsOnlyActiveRecording()
    {
        using var settings = await CreateSettingsAsync();
        await settings.GrantRecordingConsentAsync();
        await settings.SetCaptureIntentAsync(CaptureIntent.Recording);
        var capture = new TestCaptureService(settings, CaptureState.Recording);
        using var viewModel = new SettingsViewModel(settings, capture);

        Assert.True(await viewModel.SetCaptureIntervalSecondsAsync(30));

        Assert.Equal(1, capture.StopCalls);
        Assert.Equal(1, capture.StartCalls);
        Assert.Equal(30, settings.Current.CaptureIntervalSeconds);
        Assert.Equal(CaptureIntent.Recording, settings.Current.CaptureIntent);
    }

    [Fact]
    public async Task SendRuleMutationDoesNotTouchCaptureRuntime()
    {
        using var settings = await CreateSettingsAsync();
        await settings.GrantRecordingConsentAsync();
        await settings.SetCaptureIntentAsync(CaptureIntent.Recording);
        var capture = new TestCaptureService(settings, CaptureState.Recording);
        using var viewModel = new SettingsViewModel(settings, capture);

        var added = await viewModel.AddExclusionRuleAsync(
            "Browser",
            enabled: true,
            CaptureExclusionRuleScope.Application,
            ApplicationIdentityKind.ExecutableName,
            "browser.exe",
            windowTitleMatchKind: null,
            pattern: null);

        Assert.True(added);
        Assert.Equal(0, capture.StopCalls);
        Assert.Equal(CaptureIntent.Recording, settings.Current.CaptureIntent);
        Assert.Equal(2, viewModel.ExclusionRuleCount);
        Assert.Equal("不发送规则已添加。", viewModel.RuleMutationNoticeText);
    }

    [Fact]
    public async Task WinDayFlowPresetCanBeDeleted()
    {
        using var settings = await CreateSettingsAsync();
        var capture = new TestCaptureService(settings, CaptureState.Stopped);
        using var viewModel = new SettingsViewModel(settings, capture);
        var preset = Assert.Single(viewModel.ExclusionRules);

        Assert.True(await viewModel.DeleteExclusionRuleAsync(preset));

        Assert.Empty(viewModel.ExclusionRules);
        Assert.Empty(settings.Current.Evidence.SendRules.Rules);
    }

    [Fact]
    public async Task CaptureFailureIsPresentedWithoutChangingSettings()
    {
        using var settings = await CreateSettingsAsync();
        await settings.GrantRecordingConsentAsync();
        var capture = new TestCaptureService(settings, CaptureState.Stopped)
        {
            Failure = new InvalidOperationException("capture failed"),
        };
        using var viewModel = new SettingsViewModel(settings, capture);

        Assert.False(await viewModel.SetCaptureEnabledAsync(enabled: true));

        Assert.True(viewModel.HasError);
        Assert.Equal(CaptureIntent.Stopped, settings.Current.CaptureIntent);
    }

    private static async Task<AppSettingsService> CreateSettingsAsync()
    {
        var service = new AppSettingsService(
            new InMemorySettingsRepository(AppSettings.Default));
        await service.InitializeAsync();
        return service;
    }

    private sealed class TestCaptureService(
        AppSettingsService settings,
        CaptureState initialState) : ICaptureService
    {
        private ulong _sequence;

        public CaptureStatus CurrentStatus { get; private set; } =
            CreateStatus(initialState, 0);

        public Exception? Failure { get; init; }

        public int StartCalls { get; private set; }

        public int StopCalls { get; private set; }

        public event EventHandler<CaptureStatusChangedEventArgs>? StatusChanged;

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCalls++;
            ThrowIfFailed();
            await settings.SetCaptureIntentAsync(CaptureIntent.Recording, cancellationToken);
            Transition(CaptureState.Recording);
        }

        public async Task PauseAsync(CancellationToken cancellationToken = default)
        {
            await settings.SetCaptureIntentAsync(CaptureIntent.Paused, cancellationToken);
            Transition(CaptureState.Paused);
        }

        public Task ResumeAsync(CancellationToken cancellationToken = default) =>
            StartAsync(cancellationToken);

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCalls++;
            ThrowIfFailed();
            await settings.SetCaptureIntentAsync(CaptureIntent.Stopped, cancellationToken);
            Transition(CaptureState.Stopped);
        }

        private void Transition(CaptureState state)
        {
            var previous = CurrentStatus;
            CurrentStatus = CreateStatus(state, ++_sequence);
            StatusChanged?.Invoke(
                this,
                new CaptureStatusChangedEventArgs(previous, CurrentStatus));
        }

        private void ThrowIfFailed()
        {
            if (Failure is not null)
            {
                throw Failure;
            }
        }

        private static CaptureStatus CreateStatus(CaptureState state, ulong sequence) =>
            state == CaptureState.Faulted
                ? new CaptureStatus(
                    state,
                    DateTimeOffset.UtcNow,
                    "录制组件发生错误。",
                    sequence,
                    ErrorCode: CaptureErrorCode.Unknown)
                : new CaptureStatus(
                    state,
                    DateTimeOffset.UtcNow,
                    Sequence: sequence);
    }

    private sealed class InMemorySettingsRepository(AppSettings initial)
        : IAppSettingsRepository
    {
        private AppSettings _current = initial;

        public Task<AppSettings> GetAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_current);
        }

        public Task SaveAsync(
            AppSettings expected,
            AppSettings proposed,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(expected, _current);
            _current = proposed;
            return Task.CompletedTask;
        }
    }
}
