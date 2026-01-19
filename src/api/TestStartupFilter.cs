// Small test-only startup filter used from Program when running under Testing
namespace Farm.Web.Api.Testing
{
    internal sealed class TestStartupFilter(System.Action onConfigure) : IStartupFilter
    {
        private readonly System.Action _onConfigure = onConfigure ?? throw new ArgumentNullException(nameof(onConfigure));

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                try
                {
                    _onConfigure();
                }
                catch
                {
                }

                next(app);
            };
        }
    }
}
