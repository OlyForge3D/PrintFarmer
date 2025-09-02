using Farm.Web.Client;
using Farm.Web.Client.Services;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");

// Load configuration
var configHttp = new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) };

using var response = await configHttp.GetAsync(new Uri("appsettings.json", UriKind.Relative));
using var stream = await response.Content.ReadAsStreamAsync();
builder.Configuration.AddJsonStream(stream);

if (builder.HostEnvironment.IsDevelopment())
{
    using var devResponse = await configHttp.GetAsync(new Uri("appsettings.Development.json", UriKind.Relative));
    if (devResponse.IsSuccessStatusCode)
    {
        using var devStream = await devResponse.Content.ReadAsStreamAsync();
        builder.Configuration.AddJsonStream(devStream);
    }
}

configHttp.Dispose();

// Configure HttpClient with API base URL
var apiBaseUrl = builder.Configuration["ApiBaseUrl"] ?? builder.HostEnvironment.BaseAddress;
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(apiBaseUrl) });

builder.Services.AddScoped<ToastService>();
builder.Services.AddScoped<RealtimeService>();
builder.Services.AddScoped<JobQueueService>();
builder.Services.AddScoped<GcodeLibraryService>();
builder.Services.AddScoped<PrinterService>();

await builder.Build().RunAsync();
