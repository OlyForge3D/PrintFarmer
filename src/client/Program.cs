using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Farm.Web.Client;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");

// Load configuration
var configHttp = new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) };

using var response = await configHttp.GetAsync("appsettings.json");
using var stream = await response.Content.ReadAsStreamAsync();
builder.Configuration.AddJsonStream(stream);

if (builder.HostEnvironment.IsDevelopment())
{
    using var devResponse = await configHttp.GetAsync("appsettings.Development.json");
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

await builder.Build().RunAsync();
