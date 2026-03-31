using System;
using Microsoft.AspNetCore.Builder;

namespace Farm.Web.Api.Tests;

internal sealed class ActionStartupFilter(Action onConfigure) : Microsoft.AspNetCore.Hosting.IStartupFilter
{
    private readonly Action _onConfigure = onConfigure ?? throw new ArgumentNullException(nameof(onConfigure));

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
