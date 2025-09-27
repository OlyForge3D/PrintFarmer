using System.Text.Json;
using System.Threading;
using Farm.Web.Api.Settings;

namespace Farm.Web.Api.Services
{
    public interface IAppSettingsService
    {
        AppSettings Current { get; }
        Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
        Task ReloadAsync(CancellationToken cancellationToken = default);
    }

    public class AppSettingsService : IAppSettingsService, IDisposable
    {
        private readonly string _settingsPath;
        private AppSettings _current;
        private static readonly JsonSerializerOptions CachedOptions = new JsonSerializerOptions { WriteIndented = true };
        private readonly SemaphoreSlim _lock = new(1, 1);

        public AppSettings Current => _current;

        public AppSettingsService(string settingsPath)
        {
            _settingsPath = settingsPath;
            _current = LoadFromDisk();
        }

        public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                var json = JsonSerializer.Serialize(settings, CachedOptions);
                await File.WriteAllTextAsync(_settingsPath, json, cancellationToken);
                _current = settings;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task ReloadAsync(CancellationToken cancellationToken = default)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                _current = LoadFromDisk();
            }
            finally
            {
                _lock.Release();
            }
        }

        private AppSettings LoadFromDisk()
        {
            if (!File.Exists(_settingsPath))
            {
                return new AppSettings();
            }
            var json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }

        public void Dispose()
        {
            // Dispose SemaphoreSlim
            _lock?.Dispose();
        }
    }
}
