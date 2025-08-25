using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Farm.Web.Client;
using Microsoft.AspNetCore.Components;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<ToastService>();
builder.Services.AddScoped<RealtimeService>();

await builder.Build().RunAsync();
