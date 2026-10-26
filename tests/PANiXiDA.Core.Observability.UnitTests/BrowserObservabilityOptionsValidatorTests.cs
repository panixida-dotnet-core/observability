using PANiXiDA.Core.Observability.Options.BrowserObservability;

namespace PANiXiDA.Core.Observability.UnitTests;

public sealed class BrowserObservabilityOptionsValidatorTests
{
    [Theory(DisplayName = "Browser options should reject missing or blank service names")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void Validate_Should_Fail_When_ServiceNameIsBlank(string serviceName)
    {
        var options = new BrowserObservabilityOptions { ServiceName = serviceName, Endpoint = "/telemetry" };
        var validator = new BrowserObservabilityOptionsValidator();

        var result = validator.Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldContain("OTEL_SERVICE_NAME must be configured when OTEL_EXPORTER_OTLP_ENDPOINT is set.");
    }

    [Fact(DisplayName = "Browser options should accept a configured service name")]
    public void Validate_Should_Succeed_When_ServiceNameIsConfigured()
    {
        var options = new BrowserObservabilityOptions { ServiceName = "admin-client", Endpoint = "/telemetry" };
        var validator = new BrowserObservabilityOptionsValidator();

        var result = validator.Validate(null, options);

        result.Succeeded.ShouldBeTrue();
    }

    [Theory(DisplayName = "Browser options should ignore export settings when endpoint is absent")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Validate_Should_Succeed_When_ExportIsDisabled(string? endpoint)
    {
        var options = new BrowserObservabilityOptions { Endpoint = endpoint, MaxRequestSizeBytes = 1 };
        var validator = new BrowserObservabilityOptionsValidator();

        var result = validator.Validate(null, options);

        result.Succeeded.ShouldBeTrue();
    }

    [Theory(DisplayName = "Browser options should reject malformed or non-HTTP endpoints")]
    [InlineData("ftp://collector.example.test/")]
    [InlineData("file:///telemetry")]
    [InlineData("http://[")]
    public void Validate_Should_Fail_When_EndpointIsUnsupported(string endpoint)
    {
        var options = new BrowserObservabilityOptions { ServiceName = "admin-client", Endpoint = endpoint };
        var validator = new BrowserObservabilityOptionsValidator();

        var result = validator.Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldContain($"{BrowserObservabilityOptions.EndpointKey} must be a relative URI or an absolute HTTP/HTTPS URI.");
    }

    [Theory(DisplayName = "Browser options should accept supported request size limits")]
    [InlineData(null)]
    [InlineData(750000)]
    [InlineData(1048576)]
    [InlineData(268435456)]
    public void Validate_Should_Succeed_When_RequestSizeIsSupported(int? limit)
    {
        var options = new BrowserObservabilityOptions { ServiceName = "admin-client", Endpoint = "/telemetry", MaxRequestSizeBytes = limit };
        var validator = new BrowserObservabilityOptionsValidator();

        var result = validator.Validate(null, options);

        result.Succeeded.ShouldBeTrue();
    }
}
