using System;

namespace Farm.Web.Api.Services.TestHelpers
{
    // Internal test helper factory to create runtime PrinterInfo-like objects for tests.
    // Marked internal so tests (with InternalsVisibleTo) can call it directly instead of using fragile reflection.
    internal static class PrinterInfoFactory
    {
        public static object Create(string name, string? manufacturer = null, string? model = null, string? firmware = null, string? version = null)
        {
            // Try to instantiate the shared PrinterInfo type (moved from API to shared project).
            System.Reflection.Assembly sharedAssembly = typeof(Farm.Infrastructure.Contracts.Printers.PrusaLink.PrinterInfo).Assembly;
            Type t = sharedAssembly.GetType("Farm.Infrastructure.Contracts.Printers.PrusaLink.PrinterInfo") ?? throw new InvalidOperationException("Shared PrinterInfo type not found");

            // Instantiate and set properties permissively
            object inst = Activator.CreateInstance(t)!;

            System.Reflection.PropertyInfo? p = t.GetProperty("Name");
            p?.SetValue(inst, name);

            p = t.GetProperty("Manufacturer");
            p?.SetValue(inst, manufacturer);

            p = t.GetProperty("Model");
            p?.SetValue(inst, model);

            p = t.GetProperty("Firmware");
            p?.SetValue(inst, firmware);

            p = t.GetProperty("Version");
            p?.SetValue(inst, version);

            return inst;
        }
    }
}
