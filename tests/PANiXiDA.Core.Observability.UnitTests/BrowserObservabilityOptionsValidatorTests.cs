using PANiXiDA.Core.Observability.WebAssembly.Options;

namespace PANiXiDA.Core.Observability.UnitTests;

public sealed class BrowserObservabilityOptionsValidatorTests
{
    [Theory(DisplayName = "Browser options should reject missing or blank service names")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void Validate_Should_Fail_When_ServiceNameIsBlank(string serviceName)
    {
        var options = new BrowserObservabilityOptions { ServiceName = serviceName };
        var validator = new BrowserObservabilityOptionsValidator();

        var result = validator.Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldContain("OTEL_SERVICE_NAME must be configured when OTEL_EXPORTER_OTLP_ENDPOINT is set.");
    }

    [Fact(DisplayName = "Browser options should accept a configured service name")]
    public void Validate_Should_Succeed_When_ServiceNameIsConfigured()
    {
        var options = new BrowserObservabilityOptions { ServiceName = "admin-client" };
        var validator = new BrowserObservabilityOptionsValidator();

        var result = validator.Validate(null, options);

        result.Succeeded.ShouldBeTrue();
    }
}
