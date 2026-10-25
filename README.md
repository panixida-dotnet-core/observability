# PANiXiDA.Core.Observability

`PANiXiDA.Core.Observability` is a .NET package that adds PANiXiDA host observability conventions on top of OpenTelemetry.

It is intended for ASP.NET Core services that need consistent logging, metrics, tracing, resource metadata, and OTLP exporter wiring without duplicating bootstrap code in every service.

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
- ASP.NET Core application using `WebApplicationBuilder`
- OTLP-compatible collector or backend when telemetry export is enabled

## Installation

```xml
<ItemGroup>
  <PackageReference Include="PANiXiDA.Core.Observability" Version="1.1.0" />
</ItemGroup>
```

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

Install `PANiXiDA.Core.Observability.WebAssembly` 1.1.0 in the client project.
The existing `PANiXiDA.Core.Observability` package remains the ASP.NET Core server
adapter and keeps its existing API. The browser package does not reference
Npgsql or the ASP.NET Core server shared framework.

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
or EventPipe dumps. The package does not change consuming projects' build properties.

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
configuration validation, and public argument validation.
