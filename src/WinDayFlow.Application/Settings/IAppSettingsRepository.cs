namespace WinDayFlow.Application.Settings;

public interface IAppSettingsRepository
{
    Task<AppSettings> GetAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(
        AppSettings expected,
        AppSettings proposed,
        CancellationToken cancellationToken = default);
}
