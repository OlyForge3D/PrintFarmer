using System;

namespace Farm.Web.Api.Services.TestHelpers
{
    // Internal test helper factory to create runtime PrinterInfo-like objects for tests.
    // Marked internal so tests (with InternalsVisibleTo) can call it directly instead of using fragile reflection.
    internal static class PrinterInfoFactory
    {
        public static object Create(string name, string? manufacturer = null, string? model = null, string? firmware = null, string? version = null)
        {
            // Try to instantiate the API's PrinterInfo type if present.
            var apiAssembly = typeof(PrinterInfoFactory).Assembly;
            var t = apiAssembly.GetType("Farm.Web.Api.Services.PrinterInfo");
            if (t == null)
            {
                throw new InvalidOperationException("API PrinterInfo type not found");
            }

            // Instantiate and set properties permissively
            var inst = Activator.CreateInstance(t)!;

            var p = t.GetProperty("Name");
            if (p != null)
            {
                p.SetValue(inst, name);
            }

            p = t.GetProperty("Manufacturer");
            if (p != null)
            {
                p.SetValue(inst, manufacturer);
            }

            p = t.GetProperty("Model");
            if (p != null)
            {
                p.SetValue(inst, model);
            }

            p = t.GetProperty("Firmware");
            if (p != null)
            {
                p.SetValue(inst, firmware);
            }

            p = t.GetProperty("Version");
            if (p != null)
            {
                p.SetValue(inst, version);
            }

            return inst;
        }
    }
}
