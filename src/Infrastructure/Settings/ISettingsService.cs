using System.Collections.Generic;
using Microsoft.Extensions.Configuration;

namespace Farm.Infrastructure.Settings
{
    public interface ISettingsService
    {
        T Get<T>() where T : class;
        object GetByKey(string key);
        IEnumerable<object> All { get; }
        void Reload(IConfiguration config);
        IEnumerable<SettingMetadata> GetAllMetadata();
        void Save<T>(T settings) where T : class, IAppSetting;
    }
}
