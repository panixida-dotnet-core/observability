# PANiXiDA.Core.Observability

`PANiXiDA.Core.Observability` is an MSBuild SDK package that adds PANiXiDA host observability conventions on top of OpenTelemetry.

The same package and assembly support ASP.NET Core servers and Blazor WebAssembly
clients. Each host calls `builder.AddObservability()` using the extension for its
builder. The SDK selects server or browser NuGet dependencies before restore.

## Status

[![CI](https://github.com/panixida-dotnet-core/observability/actions/workflows/ci.yml/badge.svg)](https://github.com/panixida-dotnet-core/observability/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/PANiXiDA.Core.Observability.svg)](https://www.nuget.org/packages/PANiXiDA.Core.Observability)
[![NuGet downloads](https://img.shields.io/nuget/dt/PANiXiDA.Core.Observability.svg)](https://www.nuget.org/packages/PANiXiDA.Core.Observability)
[![Target Framework](https://img.shields.io/badge/target-net10.0-512BD4)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/github/license/panixida-dotnet-core/observability.svg)](LICENSE)

## Overview

The package configures OpenTelemetry for ASP.NET Core hosts with:

- resource metadata based on OpenTelemetry resource defaults, application assembly version, and machine name;
- ASP.NET Core, HTTP client, runtime, and Npgsql instrumentation;
- Wolverine and Quartz 4.x metrics and traces when the application uses these libraries;
- OpenTelemetry logging with formatted messages, scopes, and parsed state values;
- OTLP exporters for logs, metrics, and traces;
- configuration-driven OTLP exporter options through standard OpenTelemetry keys.

## Requirements

- .NET 10 SDK
- ASP.NET Core application using `WebApplicationBuilder`, or a Blazor WebAssembly
  application using `WebAssemblyHostBuilder`
- OTLP-compatible collector or backend when telemetry export is enabled

## Installation

Add the SDK version to the existing `global.json`:

```json
{
  "msbuild-sdks": {
    "PANiXiDA.Core.Observability": "2.0.0"
  }
}
```

Add the same SDK reference to the server and client `.csproj` files while keeping
their existing primary SDK and target framework:

```xml
<Sdk Name="PANiXiDA.Core.Observability" />
```

For example, an ASP.NET Core host starts with:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <Sdk Name="PANiXiDA.Core.Observability" />
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
```

Version 2 changes the installation contract. Remove the old ordinary
`PackageReference` and its central `PackageVersion` when migrating from 1.x.
An ordinary package reference fails the build with migration guidance (`PANOBS002`).
The C# registration call remains unchanged. MSBuild resolves the SDK through the
configured NuGet feeds; it must be published or available locally before restore,
formatting, build, or IDE project evaluation can succeed.

## Quick Start

```csharp
using PANiXiDA.Core.Observability;

var builder = WebApplication.CreateBuilder(args);

builder.AddObservability();

var app = builder.Build();

app.MapGet("/", () => Results.Ok());

app.Run();
```

`service.version` is resolved from `Assembly.GetEntryAssembly()`, which points to the application entry assembly for a normal ASP.NET Core host.

## Usage

### Typical ASP.NET Core Host

```csharp
using PANiXiDA.Core.Observability;

var builder = WebApplication.CreateBuilder(args);

builder.AddObservability();

builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapHealthChecks("/health");
app.MapGet("/orders/{id:guid}", (Guid id) => Results.Ok(new { id }));

app.Run();
```

Set the service name outside the package with OpenTelemetry resource configuration:

```bash
OTEL_SERVICE_NAME=orders-api
```

## Configuration

### Service Name

Set `service.name` through the standard OpenTelemetry resource key:

```bash
OTEL_SERVICE_NAME=orders-api
```

or:

```bash
OTEL_RESOURCE_ATTRIBUTES=service.name=orders-api
```

In `appsettings.json`, use flat OpenTelemetry keys such as `OTEL_SERVICE_NAME`; nested keys are not equivalent.

The package does not define a custom `OpenTelemetry:ServiceName` key. If no OpenTelemetry service name is configured, `builder.Environment.ApplicationName` is used as the fallback service name required by `AddService`.

### OTLP Exporter

The package does not hard-code OTLP endpoint, protocol, headers, or timeout values. Configure them through standard OpenTelemetry configuration keys.

### Wolverine

`AddObservability()` subscribes to the `Wolverine` activity source and the
`Wolverine*` meters. Wolverine names its meters `Wolverine:{ApplicationName}`, so
the wildcard also collects metrics from application-specific meter names. These
signals use the same configured OTLP exporters as the rest of the application.
No Wolverine package dependency or additional registration is required in this
observability package; applications without Wolverine emit no Wolverine signals.

Wolverine logs already use the standard `ILogger` pipeline and are exported by
the existing OpenTelemetry logging provider. Normal application logging filters
still apply, including message-type categories used by Wolverine handlers.
Enabling metrics and traces does not change logging levels.

The built-in `wolverine-dead-letter-queue` metric counts messages moved to a dead
letter queue; it does not measure the number of messages currently stored there.
Exporting it does not add a DLQ backlog gauge or configure alert thresholds.

See [Wolverine instrumentation and metrics](https://wolverinefx.net/guide/logging).

### Quartz

`AddObservability()` subscribes to the `Quartz` activity source and meter used by
Quartz 4.x. Job and scheduler metrics and traces use the existing OTLP exporters;
this package does not require a Quartz dependency or a separate instrumentation
package. Quartz 3.x instrumentation is not covered by these subscriptions.

Quartz logs already use `ILogger` and follow the configured logging filters.
Exporting the built-in signals does not add last-success tracking, job deadlines,
or alert rules.

See [Quartz observability](https://www.quartz-scheduler.net/documentation/quartz-4.x/packages/opentelemetry-integration).

### Production Minimum

No OTLP exporter key is mandatory when the collector is available at the OpenTelemetry defaults. In production, configure only the values that differ from those defaults.

Usually required for a deployed service:

- `OTEL_SERVICE_NAME`, so the service has a stable `service.name`;
- `OTEL_EXPORTER_OTLP_ENDPOINT` when the collector is not available at the default local endpoint.

Minimal `appsettings.json` example for a service exporting to a gRPC collector:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "OTEL_SERVICE_NAME": "orders-api",
  "OTEL_EXPORTER_OTLP_ENDPOINT": "http://otel-collector:4317"
}
```

`OTEL_SERVICE_NAME` can be provided either as a real environment variable or as a flat `appsettings.json` key. For containers and Kubernetes, prefer deployment environment variables so the same package binary can run with different service names.

Keep OTEL keys flat in `appsettings.json`. A nested structure such as `OTEL:EXPORTER:OTLP:ENDPOINT` produces a different `IConfiguration` key and is not the same as `OTEL_EXPORTER_OTLP_ENDPOINT`.

Defaults provided by OpenTelemetry are usually enough for processor and reader settings:

- `OTEL_EXPORTER_OTLP_PROTOCOL` defaults to `grpc`.
- `OTEL_EXPORTER_OTLP_ENDPOINT` defaults to `http://localhost:4317` for gRPC and `http://localhost:4318` for HTTP/protobuf.
- `OTEL_EXPORTER_OTLP_TIMEOUT` defaults to `10000` milliseconds.
- metrics export interval defaults to `60000` milliseconds.
- tracing and logging exporters use batch processing by default.

Set `OTEL_EXPORTER_OTLP_PROTOCOL` only when using HTTP/protobuf, typically with port `4318`:

```json
{
  "OTEL_EXPORTER_OTLP_ENDPOINT": "http://otel-collector:4318",
  "OTEL_EXPORTER_OTLP_PROTOCOL": "http/protobuf"
}
```

### Client Cancellation Logs

`AddObservability` registers an HTTP context accessor and normalizes cancellation logs before OTLP batch export. An `Error` record becomes `Information` only when the current HTTP request's `RequestAborted` token is canceled and the exception is either `OperationCanceledException` (including `TaskCanceledException`) or a nonempty `AggregateException` containing only cancellations, including nested aggregates.

The record is retained with its original message, exception, stack trace, attributes, scopes, timestamp, and trace/span IDs. Npgsql cancellations represented by `OperationCanceledException` with an inner `PostgresException` follow the same rule.

Background cancellations, cancellations without an aborted request, database timeouts, bare database exceptions, unknown wrappers, empty or mixed aggregates, and unrelated application failures retain their original level. Other levels, including `Critical`, are unchanged. This normalization applies only to OpenTelemetry logs; console and other logging providers are unaffected. Application logging filters still apply before this processor.

### Optional Tuning

Set these only when your backend or operating requirements need them:

```json
{
  "OTEL_EXPORTER_OTLP_HEADERS": "api-key=secret",
  "OTEL_EXPORTER_OTLP_TIMEOUT": "10000",
  "OTEL_BSP_SCHEDULE_DELAY": "5000",
  "OTEL_BSP_EXPORT_TIMEOUT": "30000",
  "OTEL_BLRP_SCHEDULE_DELAY": "5000",
  "OTEL_BLRP_EXPORT_TIMEOUT": "30000",
  "OTEL_METRIC_EXPORT_INTERVAL": "60000",
  "OTEL_METRIC_EXPORT_TIMEOUT": "30000"
}
```

The same OTLP values can be provided as environment variables:

```bash
OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317
OTEL_EXPORTER_OTLP_HEADERS=api-key=secret
OTEL_EXPORTER_OTLP_TIMEOUT=10000
```

Use signal-specific keys in `appsettings.json` only when logs, metrics, and traces need different endpoints. These keys are supported by OpenTelemetry .NET `UseOtlpExporter`; if all signals use the same collector, prefer `OTEL_EXPORTER_OTLP_ENDPOINT`.

```json
{
  "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT": "http://traces-collector:4317",
  "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT": "http://metrics-collector:4317",
  "OTEL_EXPORTER_OTLP_LOGS_ENDPOINT": "http://logs-collector:4317"
}
```

For HTTP/protobuf signal-specific endpoints, provide the full signal path and configure the protocol:

```json
{
  "OTEL_EXPORTER_OTLP_PROTOCOL": "http/protobuf",
  "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT": "http://traces-collector:4318/v1/traces",
  "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT": "http://metrics-collector:4318/v1/metrics",
  "OTEL_EXPORTER_OTLP_LOGS_ENDPOINT": "http://logs-collector:4318/v1/logs"
}
```

OpenTelemetry .NET reads these keys through `IConfiguration`, so they may come from environment variables, command-line arguments, or `appsettings.json`.

## Public API

### AddObservability

```csharp
public static WebApplicationBuilder AddObservability(
    this WebApplicationBuilder builder)
```

Behavior:

- validates `builder`;
- configures OpenTelemetry resource metadata using the entry assembly version when available;
- adds ASP.NET Core, HTTP client, Npgsql, and runtime instrumentation;
- subscribes to Wolverine and Quartz 4.x metrics and traces without requiring dependencies on these libraries;
- adds the OTLP exporter for logging, metrics, and tracing through OpenTelemetry `UseOtlpExporter`;
- returns the same `WebApplicationBuilder` instance for chaining.

## Production Notes

- Run an OTLP collector or backend outside this package.
- Keep endpoints, protocols, headers, and credentials in environment-specific configuration.
- Prefer HTTP/protobuf on port `4318` or gRPC on port `4317`, depending on the collector setup.
- Ensure application logging filters are configured intentionally, because OpenTelemetry logging follows normal `ILogger` filtering.
- Treat exporter headers as secrets when they contain tokens or API keys.

## Project Structure

```text
.
|-- src/
|   `-- PANiXiDA.Core.Observability/
|-- tests/
|   `-- PANiXiDA.Core.Observability.UnitTests/
|-- Directory.Build.props
|-- Directory.Build.targets
|-- Directory.Packages.props
|-- global.json
|-- version.json
|-- README.md
|-- LICENSE
`-- icon.png
```

## Development

### Build

```bash
dotnet restore
dotnet build --configuration Release
```

### Format

```bash
dotnet format
```

### Test

```bash
dotnet test --configuration Release
```

### Coverage

```bash
dotnet test --configuration Release --coverage --coverage-output coverage.xml --coverage-output-format xml
```

### Pack

```bash
dotnet pack --configuration Release
```

### Continuous integration

Every pull request and push to `main` runs formatting, tests, and mandatory
SonarQube analysis. Publishing from `main` starts only after the SonarQube
Quality Gate succeeds.

## License

This project is licensed under the Apache-2.0 license.

See the [LICENSE](LICENSE) file for details.

## Blazor WebAssembly

Reference the `PANiXiDA.Core.Observability` 2.0.0 SDK in both server and client projects.
There is one source project, one assembly, and one NuGet package, all targeting
`net10.0`. The server keeps its existing API and complete instrumentation,
including `AddAspNetCoreInstrumentation()`. The client keeps `net10.0`, so the
server can continue referencing the client for prerendering and Interactive Auto.

```csharp
using PANiXiDA.Core.Observability.WebAssembly;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.AddObservability();
// Register the application and HTTP clients as usual.
var host = builder.Build();
host.UseObservability();
await host.RunAsync();
```

`AddObservability` follows the Microsoft Blazor client defaults: component metrics
and traces, standard HTTP instrumentation, logging, and protobuf OTLP export.
The SDK keeps its default batch sizes and export intervals. Register additional
application sources with `AddSource` / `AddMeter` when needed. `UseObservability`
starts the standard SDK initializer on .NET 10 and is a no-op when export is disabled.
Enable the runtime features explicitly in the consuming Blazor client `.csproj`:

```xml
<PropertyGroup>
  <MetricsSupport>true</MetricsSupport>
  <EventSourceSupport>true</EventSourceSupport>
  <HttpActivityPropagationSupport>true</HttpActivityPropagationSupport>
</PropertyGroup>
```

`MetricsSupport` enables the metrics APIs, `EventSourceSupport` preserves
EventSource diagnostics, and `HttpActivityPropagationSupport` enables the runtime's
HTTP activities and W3C propagation. These settings do not enable CPU profilers
or EventPipe dumps.

### Browser build compatibility

The SDK selects browser dependencies when `UsingMicrosoftNETSdkBlazorWebAssembly`
is true, and server dependencies otherwise. The browser graph contains HTTP and
OTLP telemetry plus Blazor WebAssembly, without Npgsql or server instrumentation.
The server adapter adds ASP.NET Core, Runtime, and Npgsql instrumentation, without
adding WebAssembly packages. An application can still have platform dependencies
for other reasons: a Blazor Web App host references its WASM client for prerendering.

The existing repository `Directory.Build.props` and `Directory.Build.targets` are
packed as `Sdk/Sdk.props` and `Sdk/Sdk.targets`. A generated `Sdk/Dependencies.props`
gets dependency versions from `Directory.Packages.props` and host classifications
from the library's package references. No separately maintained SDK source files
or version table are required. Repository build and package metadata are guarded
by the original file names and do not overwrite consumer settings.

Both ordinary and centrally managed package versions are supported, including
`CentralPackageVersionOverrideEnabled=false`. Existing direct references and central
versions are preserved; missing dependencies get SDK defaults. A resolved dependency
below the SDK's supported stable minimum fails with `PANOBS001` instead of being
silently downgraded. No override of `DisableTransitiveFrameworkReferences` is needed.

There is one DLL with both implementations. Client code uses the WebAssembly
extension namespace shown above; browser code must not call server-only APIs.
Release trimming removes unreachable server code. The DLL still contains references
to both platforms before trimming, so reflection over all of its method signatures
requires the corresponding platform assemblies; the package does not promise that
unrelated assembly scanning libraries can resolve unused platform-specific APIs.

Configure the client in its public `wwwroot/appsettings.json`, for example:

```json
{
  "OTEL_SERVICE_NAME": "my-client",
  "OTEL_EXPORTER_OTLP_ENDPOINT": "/telemetry",
  "OpenTelemetry": {
    "Otlp": {
      "MaxRequestSizeBytes": 1048576
    }
  }
}
```

Without `OTEL_EXPORTER_OTLP_ENDPOINT`, no exporter is registered. When an endpoint
is set, `OTEL_SERVICE_NAME` is required. As in the Microsoft template, the service
name is also the instance ID, grouping browser tabs under one service instance.
Relative endpoints resolve against the app's base address; absolute HTTP endpoints
are also supported. The library does not assume a BFF route.

HTTP spans, duration measurements, and W3C context propagation use the standard
.NET HTTP instrumentation, including directly constructed `HttpClient` instances.
Propagation follows the runtime defaults; external APIs must permit tracing headers
in their CORS policy. The OTLP SDK suppresses tracing of its own export requests.

The exporter appends `v1/logs`, `v1/metrics`, and `v1/traces` to the configured
endpoint. The application can expose that path through its BFF and forward
requests to an OTLP HTTP receiver. Do not put
collector credentials in client configuration. Use separate client service names
and treat incoming browser resource attributes as untrusted.

OpenTelemetry 1.19.1 supplies protobuf serialization and browser-compatible OTLP
delivery without a custom transport or HttpClientFactory override. Delivery is
best effort; a failed receiver or closing the tab can lose records.

`OpenTelemetry:Otlp:MaxRequestSizeBytes` is an optional package configuration key
mapped to the SDK's request size limit. If omitted, the SDK default applies.
OpenTelemetry 1.19.1 accepts 750,000 through 268,435,456 bytes; unsupported values
fail provider initialization. Oversized batches are dropped by the SDK rather
than sent. Keep the client limit at or below the BFF body limit, and the BFF limit
at or below the Collector receiver limit. Tactical Heroes configures all three
to 1 MiB. Reverse-proxy and HTTP server limits still apply.

### Tests

The native test projects cover server compatibility, protobuf export for all
signals at configurable endpoints, disabled export, HTTP trace-context propagation,
configuration validation, public argument validation, actual package contents,
SDK dependency selection with and without central version management, consumer
metadata preservation, and rejection of unsupported dependency versions.
