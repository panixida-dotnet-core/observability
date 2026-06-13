using System.Reflection;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace PANiXiDA.Core.Observability.UnitTests;

public sealed class WebApplicationBuilderExtensionsTests
{
    private const string OtelServiceNameEnvironmentVariableName = "OTEL_SERVICE_NAME";

    [Fact(DisplayName = "AddObservability throws when builder is null")]
    public void AddObservability_ShouldThrow_WhenBuilderIsNull()
    {
        var act = () => WebApplicationBuilderExtensions.AddObservability(null!);

        act.Should()
            .Throw<ArgumentNullException>()
            .WithParameterName("builder");
    }

    [Fact(DisplayName = "AddObservability returns the same builder instance")]
    public void AddObservability_ShouldReturnSameBuilder()
    {
        var builder = CreateBuilder();

        var result = builder.AddObservability();

        result.Should().BeSameAs(builder);
    }

    [Fact(DisplayName = "AddObservability configures OpenTelemetry resource metadata")]
    public void AddObservability_ShouldConfigureOpenTelemetryResourceMetadata()
    {
        var builder = CreateBuilder();
        var expectedServiceVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
            ?? "unknown";

        builder.AddObservability();

        using var app = builder.Build();
        var tracerProvider = app.Services.GetRequiredService<TracerProvider>();
        var meterProvider = app.Services.GetRequiredService<MeterProvider>();

        var traceAttributes = ToAttributes(tracerProvider.GetResource());
        var metricAttributes = ToAttributes(meterProvider.GetResource());

        traceAttributes["service.version"].Should().Be(expectedServiceVersion);
        traceAttributes["service.instance.id"].Should().Be(Environment.MachineName);
        metricAttributes["service.version"].Should().Be(expectedServiceVersion);
    }

    [Fact(DisplayName = "AddObservability uses OTEL_SERVICE_NAME instead of fallback service name")]
    public void AddObservability_ShouldUseOtelServiceNameInsteadOfFallback_WhenEnvironmentVariableIsSet()
    {
        var originalServiceName = Environment.GetEnvironmentVariable(OtelServiceNameEnvironmentVariableName);

        try
        {
            Environment.SetEnvironmentVariable(OtelServiceNameEnvironmentVariableName, "configured-service");

            var builder = CreateBuilder();

            builder.AddObservability();

            using var app = builder.Build();
            var tracerProvider = app.Services.GetRequiredService<TracerProvider>();
            var attributes = ToAttributes(tracerProvider.GetResource());

            attributes["service.name"].Should().Be("configured-service");
            attributes["service.name"].Should().NotBe("Fallback.Service");
        }
        finally
        {
            Environment.SetEnvironmentVariable(OtelServiceNameEnvironmentVariableName, originalServiceName);
        }
    }

    [Fact(DisplayName = "AddObservability uses OTEL_SERVICE_NAME from configuration instead of fallback service name")]
    public void AddObservability_ShouldUseOtelServiceNameInsteadOfFallback_WhenConfigurationValueIsSet()
    {
        var builder = CreateBuilder();

        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                [OtelServiceNameEnvironmentVariableName] = "configured-service"
            });

        builder.AddObservability();

        using var app = builder.Build();
        var tracerProvider = app.Services.GetRequiredService<TracerProvider>();
        var attributes = ToAttributes(tracerProvider.GetResource());

        attributes["service.name"].Should().Be("configured-service");
        attributes["service.name"].Should().NotBe("Fallback.Service");
    }

    [Fact(DisplayName = "AddObservability uses unknown when entry assembly version is unavailable")]
    public void AddObservability_ShouldUseUnknown_WhenEntryAssemblyVersionIsUnavailable()
    {
        GetServiceVersion(null).Should().Be("unknown");
        GetServiceVersion(new AssemblyWithoutVersion()).Should().Be("unknown");
    }

    [Fact(DisplayName = "AddObservability configures OpenTelemetry logging options")]
    public void AddObservability_ShouldConfigureOpenTelemetryLoggingOptions()
    {
        var builder = CreateBuilder();

        builder.AddObservability();

        using var app = builder.Build();
        var options = app.Services.GetRequiredService<IOptionsMonitor<OpenTelemetryLoggerOptions>>()
            .Get(Options.DefaultName);

        options.IncludeFormattedMessage.Should().BeTrue();
        options.IncludeScopes.Should().BeTrue();
        options.ParseStateValues.Should().BeTrue();
    }

    [Fact(DisplayName = "AddObservability binds OTLP exporter options from standard configuration")]
    public void AddObservability_ShouldBindOtlpExporterOptionsFromStandardConfiguration()
    {
        var builder = CreateBuilder();

        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://localhost:4318",
                ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
                ["OTEL_EXPORTER_OTLP_TIMEOUT"] = "1001"
            });

        builder.AddObservability();

        using var app = builder.Build();
        var options = app.Services.GetRequiredService<IOptionsMonitor<OtlpExporterOptions>>();

        AssertOtlpOptions(options.Get(Options.DefaultName), "http://localhost:4318", 1001);
    }

    [Fact(DisplayName = "AddObservability binds signal-specific OTLP exporter options from standard configuration")]
    public void AddObservability_ShouldBindSignalSpecificOtlpExporterOptionsFromStandardConfiguration()
    {
        var builder = CreateBuilder();

        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["OTEL_EXPORTER_OTLP_TRACES_ENDPOINT"] = "http://traces-collector:4317",
                ["OTEL_EXPORTER_OTLP_METRICS_ENDPOINT"] = "http://metrics-collector:4317",
                ["OTEL_EXPORTER_OTLP_LOGS_ENDPOINT"] = "http://logs-collector:4317"
            });

        builder.AddObservability();

        using var app = builder.Build();
        var builderOptions = GetOtlpExporterBuilderOptions(app.Services);

        AssertOtlpOptions(
            GetOtlpExporterOptions(builderOptions, "TracingOptions"),
            "http://traces-collector:4317",
            10000,
            OtlpExportProtocol.Grpc);
        AssertOtlpOptions(
            GetOtlpExporterOptions(builderOptions, "MetricsOptions"),
            "http://metrics-collector:4317",
            10000,
            OtlpExportProtocol.Grpc);
        AssertOtlpOptions(
            GetOtlpExporterOptions(builderOptions, "LoggingOptions"),
            "http://logs-collector:4317",
            10000,
            OtlpExportProtocol.Grpc);
    }

    private static WebApplicationBuilder CreateBuilder()
    {
        return WebApplication.CreateBuilder(
            new WebApplicationOptions
            {
                ApplicationName = "Fallback.Service",
                EnvironmentName = "Testing"
            });
    }

    private static Dictionary<string, object> ToAttributes(Resource resource)
    {
        return resource.Attributes.ToDictionary(attribute => attribute.Key, attribute => attribute.Value);
    }

    private static object GetOtlpExporterBuilderOptions(IServiceProvider serviceProvider)
    {
        var builderOptionsType = typeof(OtlpExporterOptions).Assembly.GetType(
            "OpenTelemetry.Exporter.OtlpExporterBuilderOptions",
            throwOnError: true)!;
        var monitorType = typeof(IOptionsMonitor<>).MakeGenericType(builderOptionsType);
        var monitor = serviceProvider.GetRequiredService(monitorType);
        var getMethod = monitorType.GetMethod(nameof(IOptionsMonitor<object>.Get), [typeof(string)])
            ?? throw new InvalidOperationException("OpenTelemetry OTLP builder options monitor does not expose Get.");

        return getMethod.Invoke(monitor, [Options.DefaultName])
            ?? throw new InvalidOperationException("OpenTelemetry OTLP builder options are not registered.");
    }

    private static OtlpExporterOptions GetOtlpExporterOptions(object builderOptions, string propertyName)
    {
        var property = builderOptions.GetType().GetProperty(propertyName)
            ?? throw new InvalidOperationException($"OpenTelemetry OTLP builder options do not expose {propertyName}.");

        return property.GetValue(builderOptions) as OtlpExporterOptions
            ?? throw new InvalidOperationException($"OpenTelemetry OTLP builder option {propertyName} has an unexpected value.");
    }

    private static string GetServiceVersion(Assembly? entryAssembly)
    {
        var method = typeof(WebApplicationBuilderExtensions).GetMethod(
            "GetServiceVersion",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("AddObservability service version resolver is missing.");

        return method.Invoke(null, [entryAssembly]) as string
            ?? throw new InvalidOperationException("AddObservability service version resolver returned an unexpected value.");
    }

    private static void AssertOtlpOptions(
        OtlpExporterOptions options,
        string endpoint,
        int timeoutMilliseconds,
        OtlpExportProtocol protocol = OtlpExportProtocol.HttpProtobuf)
    {
        options.Endpoint.Should().Be(new Uri(endpoint));
        options.Protocol.Should().Be(protocol);
        options.TimeoutMilliseconds.Should().Be(timeoutMilliseconds);
    }

    private sealed class AssemblyWithoutVersion : Assembly
    {
        public override AssemblyName GetName()
        {
            return new AssemblyName("ApplicationWithoutVersion");
        }

        public override AssemblyName GetName(bool copiedName)
        {
            return GetName();
        }
    }
}
