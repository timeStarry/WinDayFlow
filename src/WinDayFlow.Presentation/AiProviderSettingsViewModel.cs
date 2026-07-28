using CommunityToolkit.Mvvm.ComponentModel;
using WinDayFlow.Application.Ai;
using WinDayFlow.Application.Settings;

namespace WinDayFlow.Presentation.Settings;

public sealed partial class AiProviderSettingsViewModel : ObservableObject, IDisposable
{
    private readonly AiProviderConfigurationService _configuration;
    private readonly AppSettingsService _settings;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly SynchronizationContext? _synchronizationContext;
    private int _disposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyPropertyChangedFor(nameof(CanTestConnection))]
    [NotifyPropertyChangedFor(nameof(CanChangeCloudAnalysis))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string _noticeMessage = string.Empty;

    public AiProviderSettingsViewModel(
        AiProviderConfigurationService configuration,
        AppSettingsService settings)
    {
        _configuration = configuration
            ?? throw new ArgumentNullException(nameof(configuration));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _synchronizationContext = SynchronizationContext.Current;
        _configuration.ConfigurationChanged += OnConfigurationChanged;
        _settings.SettingsChanged += OnSettingsChanged;
    }

    public string DisplayName =>
        _configuration.Current?.Profile.DisplayName ?? string.Empty;

    public string BaseEndpoint =>
        _configuration.Current?.Profile.BaseEndpoint.AbsoluteUri ?? string.Empty;

    public string Model => _configuration.Current?.Profile.Model ?? string.Empty;

    public int RequestTimeoutSeconds => checked((int)(
        _configuration.Current?.Profile.RequestTimeout.TotalSeconds ?? 60));

    public bool HasProfile => _configuration.Current is not null;

    public bool HasApiKey => _configuration.Current?.HasApiKey == true;

    public bool IsValidated => _configuration.Current?.IsValidated == true;

    public bool CloudAnalysisEnabled => _settings.Current.CloudAnalysisEnabled;

    public bool CanSave => !IsBusy;

    public bool CanTestConnection =>
        !IsBusy && _configuration.Current?.IsComplete == true;

    public bool CanChangeCloudAnalysis =>
        !IsBusy && (CloudAnalysisEnabled || IsValidated);

    public bool HasError => ErrorMessage.Length > 0;

    public bool HasNotice => NoticeMessage.Length > 0;

    public string CredentialStatusText => HasApiKey
        ? "API 密钥已加密保存在当前 Windows 用户下"
        : _configuration.Current?.Profile.IsLoopback == true
            ? "本机提供方可以不使用 API 密钥"
            : "尚未保存 API 密钥";

    public string ValidationStatusText => _configuration.Current switch
    {
        null => "尚未配置分析提供方",
        { IsValidated: true, ValidatedAtUtc: { } validatedAt } =>
            $"连接已验证 · {validatedAt.ToLocalTime():g}",
        _ => "配置尚未通过连接测试",
    };

    public string CloudAnalysisStatusText => CloudAnalysisEnabled
        ? "本机待分析证据和新证据可以发送到当前提供方进行分析"
        : "云端分析保持关闭";

    public Task<bool> SaveAsync(
        string displayName,
        string baseEndpoint,
        string model,
        int requestTimeoutSeconds,
        string? replacementApiKey,
        bool clearApiKey = false,
        CancellationToken cancellationToken = default)
    {
        return RunMutationAsync(
            token => _configuration.SaveAsync(
                displayName,
                baseEndpoint,
                model,
                requestTimeoutSeconds,
                replacementApiKey,
                clearApiKey,
                token),
            "提供方配置已保存；连接验证状态已更新。",
            "无法保存提供方配置，请检查地址、模型和 API 密钥。",
            cancellationToken);
    }

    public Task<bool> TestConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        return RunMutationAsync(
            token => _configuration.TestConnectionAsync(token),
            "连接测试通过；测试仅发送了应用生成的合成图像。",
            "连接测试失败，请检查提供方地址、模型、密钥和网络。",
            cancellationToken);
    }

    public Task<bool> SetCloudAnalysisEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        return RunMutationAsync(
            token => _configuration.SetCloudAnalysisEnabledAsync(enabled, token),
            enabled
                ? "云端分析已启用。"
                : "云端分析已关闭；录制与本地数据不受影响。",
            "无法更改云端分析状态，请稍后重试。",
            cancellationToken);
    }

    public void ClearMessages()
    {
        Dispatch(() =>
        {
            ErrorMessage = string.Empty;
            NoticeMessage = string.Empty;
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetimeCancellation.Cancel();
        _configuration.ConfigurationChanged -= OnConfigurationChanged;
        _settings.SettingsChanged -= OnSettingsChanged;
    }

    private async Task<bool> RunMutationAsync(
        Func<CancellationToken, Task> mutation,
        string successNotice,
        string fallbackError,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        if (IsDisposed)
        {
            return false;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        bool entered;
        try
        {
            entered = await _mutationGate
                .WaitAsync(0, linkedCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        if (!entered)
        {
            await DispatchAsync(() =>
            {
                ErrorMessage = "另一项 AI 设置操作正在进行，请稍候。";
                NoticeMessage = string.Empty;
            }).ConfigureAwait(false);
            return false;
        }

        try
        {
            await DispatchAsync(() =>
            {
                IsBusy = true;
                ErrorMessage = string.Empty;
                NoticeMessage = string.Empty;
            }).ConfigureAwait(false);
            if (IsDisposed)
            {
                return false;
            }

            await mutation(linkedCancellation.Token).ConfigureAwait(false);
            if (IsDisposed)
            {
                return false;
            }

            await DispatchAsync(() =>
            {
                ErrorMessage = string.Empty;
                NoticeMessage = successNotice;
            }).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return false;
        }
        catch (AiProviderException exception)
        {
            await DispatchAsync(() =>
            {
                ErrorMessage = DescribeProviderError(exception.ErrorCode, fallbackError);
                NoticeMessage = string.Empty;
            }).ConfigureAwait(false);
            return false;
        }
        catch (Exception)
        {
            await DispatchAsync(() =>
            {
                ErrorMessage = fallbackError;
                NoticeMessage = string.Empty;
            }).ConfigureAwait(false);
            return false;
        }
        finally
        {
            await DispatchAsync(() => IsBusy = false).ConfigureAwait(false);
            _mutationGate.Release();
        }
    }

    private static string DescribeProviderError(
        AiProviderErrorCode errorCode,
        string fallback)
    {
        return errorCode switch
        {
            AiProviderErrorCode.AuthenticationFailed => "API 密钥未通过提供方验证。",
            AiProviderErrorCode.AccessDenied => "当前 API 密钥无权使用所选模型。",
            AiProviderErrorCode.ModelNotFound => "提供方未找到所选模型或接口。",
            AiProviderErrorCode.RateLimited => "提供方请求过于频繁，请稍后重试。",
            AiProviderErrorCode.NetworkUnavailable => "无法连接分析提供方，请检查地址和网络。",
            AiProviderErrorCode.Timeout => "分析提供方响应超时。",
            AiProviderErrorCode.InvalidResponse => "提供方返回了不兼容的结构化结果。",
            AiProviderErrorCode.InvalidConfiguration =>
                "提供方配置无效，请检查地址、模型和 API 密钥。",
            _ => fallback,
        };
    }

    private void OnConfigurationChanged(
        object? sender,
        AiProviderConfigurationChangedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        Dispatch(NotifyProviderStateChanged);
    }

    private void OnSettingsChanged(
        object? sender,
        AppSettingsChangedEventArgs eventArgs)
    {
        _ = sender;
        if (eventArgs.Previous.CloudAnalysisEnabled
            != eventArgs.Current.CloudAnalysisEnabled)
        {
            Dispatch(NotifyCloudStateChanged);
        }
    }

    private void Dispatch(Action update)
    {
        if (IsDisposed)
        {
            return;
        }

        if (_synchronizationContext is not null
            && SynchronizationContext.Current != _synchronizationContext)
        {
            _synchronizationContext.Post(
                static state =>
                {
                    var dispatch = ((AiProviderSettingsViewModel Owner, Action Update))state!;
                    if (!dispatch.Owner.IsDisposed)
                    {
                        dispatch.Update();
                    }
                },
                (this, update));
            return;
        }

        update();
    }

    private Task DispatchAsync(Action update)
    {
        if (IsDisposed)
        {
            return Task.CompletedTask;
        }

        if (_synchronizationContext is null
            || SynchronizationContext.Current == _synchronizationContext)
        {
            update();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _synchronizationContext.Post(
                static state =>
                {
                    var dispatch = ((
                        AiProviderSettingsViewModel Owner,
                        Action Update,
                        TaskCompletionSource Completion))state!;
                    try
                    {
                        if (!dispatch.Owner.IsDisposed)
                        {
                            dispatch.Update();
                        }

                        dispatch.Completion.TrySetResult();
                    }
                    catch (Exception exception)
                    {
                        dispatch.Completion.TrySetException(exception);
                    }
                },
                (this, update, completion));
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }

        return completion.Task;
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private void NotifyProviderStateChanged()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(BaseEndpoint));
        OnPropertyChanged(nameof(Model));
        OnPropertyChanged(nameof(RequestTimeoutSeconds));
        OnPropertyChanged(nameof(HasProfile));
        OnPropertyChanged(nameof(HasApiKey));
        OnPropertyChanged(nameof(IsValidated));
        OnPropertyChanged(nameof(CanTestConnection));
        OnPropertyChanged(nameof(CanChangeCloudAnalysis));
        OnPropertyChanged(nameof(CredentialStatusText));
        OnPropertyChanged(nameof(ValidationStatusText));
    }

    private void NotifyCloudStateChanged()
    {
        OnPropertyChanged(nameof(CloudAnalysisEnabled));
        OnPropertyChanged(nameof(CanChangeCloudAnalysis));
        OnPropertyChanged(nameof(CloudAnalysisStatusText));
    }
}
