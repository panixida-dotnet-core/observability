using System.Reflection;
using System.Runtime.CompilerServices;

using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace PANiXiDA.Core.Observability.UnitTests;

internal static class WebAssemblyTestHost
{
    public static WebAssemblyHostBuilder CreateBuilder(string baseAddress)
    {
        // The framework constructor bootstraps browser JavaScript. Adapter tests supply host state and use real DI.
        var builder = (WebAssemblyHostBuilder)RuntimeHelpers.GetUninitializedObject(typeof(WebAssemblyHostBuilder));
        SetProperty(builder, nameof(builder.Services), new ServiceCollection());
        SetProperty(builder, nameof(builder.Configuration), new WebAssemblyHostConfiguration());
        SetProperty(builder, nameof(builder.HostEnvironment), new TestEnvironment(baseAddress));
        SetProperty(builder, nameof(builder.RootComponents), new RootComponentMappingCollection());
        builder.Services.AddLogging();
        builder.ConfigureContainer(new DefaultServiceProviderFactory());
        return builder;
    }

    private static void SetProperty(WebAssemblyHostBuilder builder, string propertyName, object value)
    {
        var field = typeof(WebAssemblyHostBuilder).GetField($"<{propertyName}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"WebAssembly host property {propertyName} was not found.");
        field.SetValue(builder, value);
    }

    private sealed class TestEnvironment(string baseAddress) : IWebAssemblyHostEnvironment
    {
        public string BaseAddress => baseAddress;
        public string Environment => "Testing";
    }
}
