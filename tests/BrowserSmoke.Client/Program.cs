using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

using PANiXiDA.Core.Observability.WebAssembly;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<BrowserSmoke.Client.App>("#app");
builder.Services.AddHttpClient("Smoke", client => client.BaseAddress = new Uri(builder.HostEnvironment.BaseAddress));
builder.Services.AddScoped(services => services.GetRequiredService<IHttpClientFactory>().CreateClient("Smoke"));
builder.AddObservability();
var host = builder.Build();
host.UseObservability();
await host.RunAsync();
