# PANiXiDA.Core.Observability

OpenTelemetry logs, metrics, and traces for ASP.NET Core and Blazor WebAssembly
on .NET 10. One MSBuild SDK package, one DLL, and `builder.AddObservability()`.

## Status

[![CI](https://github.com/panixida-dotnet-core/observability/actions/workflows/ci.yml/badge.svg)](https://github.com/panixida-dotnet-core/observability/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/PANiXiDA.Core.Observability.svg)](https://www.nuget.org/packages/PANiXiDA.Core.Observability)
[![Target Framework](https://img.shields.io/badge/target-net10.0-512BD4)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/github/license/panixida-dotnet-core/observability.svg)](LICENSE)

## Installation

Select a published 2.x version in `global.json`:

```json
{
  "msbuild-sdks": {
    "PANiXiDA.Core.Observability": "2.0.0"
  }
}
```

Add to each host `.csproj`, keeping its existing primary SDK and `net10.0` target:

```xml
<Sdk Name="PANiXiDA.Core.Observability" />
```

When migrating from 1.x, remove the old `PackageReference` and central
`PackageVersion`. The SDK uses configured NuGet feeds and adds platform-specific
dependencies for the host.
Existing dependency versions are preserved and checked against supported minimums.

## ASP.NET Core

```csharp
using PANiXiDA.Core.Observability;

var builder = WebApplication.CreateBuilder(args);
builder.AddObservability();
var app = builder.Build();
app.MapGet("/", () => Results.Ok());
app.Run();
```

Collects ASP.NET Core, HTTP, runtime, and Npgsql telemetry, Wolverine/Quartz
sources, and application logs. Use standard OpenTelemetry configuration, for example:

```json
{
  "OTEL_SERVICE_NAME": "orders-api",
  "OTEL_EXPORTER_OTLP_ENDPOINT": "http://otel-collector:4318",
  "OTEL_EXPORTER_OTLP_PROTOCOL": "http/protobuf"
}
```

Without an explicit service name, the application name is the fallback.
Errors caused only by cancellation of an aborted HTTP request are exported at
Information level; unrelated errors keep their level.

## Blazor WebAssembly

```csharp
using PANiXiDA.Core.Observability.WebAssembly;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.AddObservability();
var host = builder.Build();
host.UseObservability();
await host.RunAsync();
```

Enable in the client `.csproj`:

```xml
<PropertyGroup>
  <MetricsSupport>true</MetricsSupport>
  <EventSourceSupport>true</EventSourceSupport>
  <HttpActivityPropagationSupport>true</HttpActivityPropagationSupport>
</PropertyGroup>
```

Configure public `wwwroot/appsettings.json`:

```json
{
  "OTEL_SERVICE_NAME": "admin-client",
  "OTEL_EXPORTER_OTLP_ENDPOINT": "/telemetry",
  "OpenTelemetry": {
    "Otlp": { "MaxRequestSizeBytes": 1048576 }
  }
}
```

Collects Blazor and HTTP telemetry and application logs. Export is disabled when
the endpoint is absent. When enabled, `OTEL_SERVICE_NAME` is required and validated
through Options; missing or blank names cause `OptionsValidationException` during
provider initialization. Relative and absolute HTTP endpoints are supported.

The exporter sends protobuf to `v1/logs`, `v1/metrics`, and `v1/traces` under the
endpoint. A BFF can forward these requests to the collector. Match the optional
request limit with BFF/collector limits; otherwise OpenTelemetry defaults apply.
Client configuration is public: keep collector credentials on the server.

HTTP instrumentation supports ordinary `HttpClient` instances. The package does
not register `IHttpClientFactory`. External APIs must allow tracing headers in CORS.
Delivery is best effort; closing the tab can lose pending telemetry.

## Compatibility

The library enables Native AOT/trimming analyzers and generated configuration
binding. Applications choose their own publish settings. Both platform APIs share
one DLL; reflection over unused platform signatures may require their assemblies.

## Development

```bash
dotnet restore
dotnet format
dotnet build --configuration Release
dotnet test --configuration Release
dotnet pack --configuration Release
```

Publishing from `main` requires successful tests, coverage, and SonarQube Quality Gate.

## License

[Apache-2.0](LICENSE).
