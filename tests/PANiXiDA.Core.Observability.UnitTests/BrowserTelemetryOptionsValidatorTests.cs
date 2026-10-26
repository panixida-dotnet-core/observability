using PANiXiDA.Core.Observability.Options.BrowserTelemetry;

namespace PANiXiDA.Core.Observability.UnitTests;

public sealed class BrowserTelemetryOptionsValidatorTests
{
    [Fact(DisplayName = "Browser relay validation should ignore settings when disabled")]
    public void Validate_Should_IgnoreSettings_When_Disabled()
    {
        var options = new BrowserTelemetryOptions { MaxRequestBodySize = -1, RequestsPerMinute = -1, RequestTimeout = TimeSpan.Zero };

        var result = new BrowserTelemetryOptionsValidator().Validate(null, options);

        result.Succeeded.ShouldBeTrue();
    }

    [Theory(DisplayName = "Browser relay validation should reject invalid collector endpoints")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative")]
    [InlineData("ftp://collector.test/")]
    [InlineData("http://collector.test/?query=true")]
    [InlineData("https://collector.test/#fragment")]
    public void Validate_Should_RejectEndpoint_When_Invalid(string? endpoint)
    {
        var options = ValidOptions();
        options.CollectorEndpoint = endpoint!;

        var result = new BrowserTelemetryOptionsValidator().Validate(null, options);

        result.Failures.ShouldNotBeNull().ShouldContain(failure => failure.StartsWith("BrowserTelemetry:CollectorEndpoint", StringComparison.Ordinal));
    }

    [Theory(DisplayName = "Browser relay validation should reject blank or unsafe service names")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("client\r\nforged-header")]
    public void Validate_Should_RejectServiceName_When_Invalid(string? serviceName)
    {
        var options = ValidOptions();
        options.ServiceName = serviceName!;

        var result = new BrowserTelemetryOptionsValidator().Validate(null, options);

        result.Failures.ShouldNotBeNull().ShouldContain(failure => failure.StartsWith("BrowserTelemetry:ServiceName", StringComparison.Ordinal));
    }

    [Theory(DisplayName = "Browser relay validation should reject invalid application paths")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative")]
    [InlineData("//external.test")]
    [InlineData("/telemetry?query")]
    [InlineData("/telemetry#fragment")]
    [InlineData("/telemetry/{route}")]
    public void Validate_Should_RejectPath_When_Invalid(string? path)
    {
        var options = ValidOptions();
        options.Path = path!;

        var result = new BrowserTelemetryOptionsValidator().Validate(null, options);

        result.Failures.ShouldNotBeNull().ShouldContain(failure => failure.StartsWith("BrowserTelemetry:Path", StringComparison.Ordinal));
    }

    [Theory(DisplayName = "Browser relay validation should reject nonpositive limits blank policies and invalid timeouts")]
    [InlineData(0, 120, 5000, null)]
    [InlineData(1048576, 0, 5000, null)]
    [InlineData(1048576, 120, 0, null)]
    [InlineData(1048576, 120, 2147483648L, null)]
    [InlineData(1048576, 120, 5000, " ")]
    public void Validate_Should_RejectSettings_When_LimitsOrPolicyAreInvalid(int bodyLimit, int requestLimit, long timeoutMilliseconds, string? policy)
    {
        var options = ValidOptions();
        options.MaxRequestBodySize = bodyLimit;
        options.RequestsPerMinute = requestLimit;
        options.RequestTimeout = TimeSpan.FromMilliseconds(timeoutMilliseconds);
        options.AuthorizationPolicy = policy;

        var result = new BrowserTelemetryOptionsValidator().Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeEmpty();
    }

    [Theory(DisplayName = "Browser relay validation should accept valid HTTP HTTPS and boundary settings")]
    [InlineData("http://collector.test/", null, 5000)]
    [InlineData("https://collector.test/base", "TelemetryPolicy", 2147483647)]
    public void Validate_Should_AcceptSettings_When_Valid(string endpoint, string? policy, int timeoutMilliseconds)
    {
        var options = ValidOptions();
        options.CollectorEndpoint = endpoint;
        options.AuthorizationPolicy = policy;
        options.RequestTimeout = TimeSpan.FromMilliseconds(timeoutMilliseconds);

        var result = new BrowserTelemetryOptionsValidator().Validate(null, options);

        result.Succeeded.ShouldBeTrue();
    }

    private static BrowserTelemetryOptions ValidOptions() => new()
    {
        Enabled = true,
        CollectorEndpoint = "http://collector.test/",
        ServiceName = "test-client"
    };
}
