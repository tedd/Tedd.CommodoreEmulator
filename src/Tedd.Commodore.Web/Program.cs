using System.Runtime.Versioning;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Tedd.Commodore.Web;

[assembly: SupportedOSPlatform("browser")]

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Base address = <base href>, so "roms/basic.bin" resolves correctly under a GitHub Pages sub-path too.
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

await builder.Build().RunAsync();
