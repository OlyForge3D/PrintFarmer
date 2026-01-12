using System;
using Microsoft.AspNetCore.Builder;

namespace Farm.Web.Api.Tests
{
    internal sealed class ActionStartupFilter : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        private readonly Action _onConfigure;

        public ActionStartupFilter(Action onConfigure)
        {
            _onConfigure = onConfigure ?? throw new ArgumentNullException(nameof(onConfigure));
        }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                try
                { _onConfigure(); }
                catch { }
                next(app);
            };
        }
    }
}
