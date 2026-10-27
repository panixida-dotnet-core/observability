using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security;
using System.Text.Json;
using System.Xml.Linq;

namespace PANiXiDA.Core.Observability.UnitTests;

public sealed class SdkPackageTests(SdkPackageFixture fixture) : IClassFixture<SdkPackageFixture>
{
    [Fact(DisplayName = "SDK package should contain one library and no unconditional dependencies")]
    public void SdkPackage_Should_ContainOneLibrary_When_Packed()
    {
        Directory.GetFiles(fixture.PackageDirectory, "*.dll", SearchOption.AllDirectories).Length.ShouldBe(1);
        File.Exists(Path.Combine(fixture.PackageDirectory, "Sdk", "Sdk.props")).ShouldBeTrue();
        File.Exists(Path.Combine(fixture.PackageDirectory, "Sdk", "Sdk.targets")).ShouldBeTrue();
        File.Exists(Path.Combine(fixture.PackageDirectory, "Sdk", "Dependencies.props")).ShouldBeTrue();
        Directory.GetFiles(fixture.PackageDirectory, "Directory.Build.*", SearchOption.AllDirectories).ShouldBeEmpty();
        var manifest = XDocument.Load(Directory.GetFiles(fixture.PackageDirectory, "*.nuspec").Single());
        manifest.Descendants().Where(element => element.Name.LocalName == "dependency").ShouldBeEmpty();
        manifest.Descendants().Where(element => element.Name.LocalName == "packageType")
            .Single().Attribute("name")!.Value.ShouldBe("MSBuildSdk");
    }

    [Theory(DisplayName = "SDK should select host dependencies and preserve consumer metadata and versions")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SdkPackage_Should_SelectHostDependencies_When_Consumed(bool browser, bool centralVersions)
    {
        var projectPath = fixture.WriteProject($"consumer-{browser}-{centralVersions}.proj", $$"""
            <Project>
              <PropertyGroup>
                <UsingMicrosoftNETSdkBlazorWebAssembly>{{browser}}</UsingMicrosoftNETSdkBlazorWebAssembly>
                <ManagePackageVersionsCentrally>{{centralVersions}}</ManagePackageVersionsCentrally>
                <CentralPackageVersionOverrideEnabled>false</CentralPackageVersionOverrideEnabled>
                <IsPackable>true</IsPackable>
                <TargetFramework>net9.0</TargetFramework>
                <Nullable>disable</Nullable>
                <ImplicitUsings>disable</ImplicitUsings>
                <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
                <Authors>ConsumerOwner</Authors><Company>ConsumerCompany</Company>
                <PackageReadmeFile>consumer.md</PackageReadmeFile><PackageIcon>consumer.png</PackageIcon>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" {{(centralVersions ? "" : "Version=\"1.20.0\"")}} />
                <PackageVersion Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.20.0" />
              </ItemGroup>
              {{fixture.Imports}}
            </Project>
            """);

        var (exitCode, output) = await SdkPackageFixture.RunAsync(fixture.RootDirectory,
            ["msbuild", projectPath, "-nologo", "-getItem:PackageReference,PackageVersion", "-getProperty:Authors,Company,PackageReadmeFile,PackageIcon,DisableTransitiveFrameworkReferences,TargetFramework,Nullable,ImplicitUsings,TreatWarningsAsErrors"]);

        exitCode.ShouldBe(0, output);
        using var document = JsonDocument.Parse(output);
        var properties = document.RootElement.GetProperty("Properties");
        properties.GetProperty("Authors").GetString().ShouldBe("ConsumerOwner");
        properties.GetProperty("Company").GetString().ShouldBe("ConsumerCompany");
        properties.GetProperty("PackageReadmeFile").GetString().ShouldBe("consumer.md");
        properties.GetProperty("PackageIcon").GetString().ShouldBe("consumer.png");
        properties.GetProperty("DisableTransitiveFrameworkReferences").GetString().ShouldBeEmpty();
        properties.GetProperty("TargetFramework").GetString().ShouldBe("net9.0");
        properties.GetProperty("Nullable").GetString().ShouldBe("disable");
        properties.GetProperty("ImplicitUsings").GetString().ShouldBe("disable");
        properties.GetProperty("TreatWarningsAsErrors").GetString().ShouldBe("false");
        var references = document.RootElement.GetProperty("Items").GetProperty("PackageReference").EnumerateArray().ToArray();
        var names = references.Select(reference => reference.GetProperty("Identity").GetString()).ToArray();
        names.Distinct().Count().ShouldBe(names.Length);
        names.Length.ShouldBe(browser ? 5 : 6);
        names.Contains("System.Threading.RateLimiting").ShouldBe(browser);
        names.Contains("Microsoft.AspNetCore.Components.WebAssembly").ShouldBe(browser);
        names.ShouldNotContain("Microsoft.Extensions.Http");
        names.Contains("Npgsql.OpenTelemetry").ShouldBe(!browser);
        names.Contains("OpenTelemetry.Instrumentation.AspNetCore").ShouldBe(!browser);
        names.Contains("OpenTelemetry.Instrumentation.Runtime").ShouldBe(!browser);
        foreach (var reference in references)
        {
            var version = reference.TryGetProperty("Version", out var value) ? value.GetString() : "";
            if (centralVersions)
            {
                version.ShouldBeNullOrEmpty();
            }
            else
            {
                version.ShouldNotBeNullOrEmpty();
            }
        }

        if (centralVersions)
        {
            var exporter = document.RootElement.GetProperty("Items").GetProperty("PackageVersion").EnumerateArray()
                .Single(item => item.GetProperty("Identity").GetString() == "OpenTelemetry.Exporter.OpenTelemetryProtocol");
            exporter.GetProperty("Version").GetString().ShouldBe("1.20.0");
        }
        else
        {
            references.Single(item => item.GetProperty("Identity").GetString() == "OpenTelemetry.Exporter.OpenTelemetryProtocol")
                .GetProperty("Version").GetString().ShouldBe("1.20.0");
        }
    }

    [Theory(DisplayName = "SDK should include the browser initializer in WASM clients and Blazor Web App hosts")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SdkPackage_Should_IncludeBrowserInitializer_When_ConsumedByBlazor(bool browser)
    {
        string directory = browser ? "browser" : "blazor-host";
        var browserDirectory = Path.Combine(fixture.RootDirectory, directory);
        Directory.CreateDirectory(browserDirectory);
        var projectPath = fixture.WriteProject(Path.Combine(directory, "Consumer.csproj"), $$"""
            <Project Sdk="{{(browser ? "Microsoft.NET.Sdk.BlazorWebAssembly" : "Microsoft.NET.Sdk.Web")}}">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <WasmBuildNative>false</WasmBuildNative>
                <RequiresAspNetWebAssets>true</RequiresAspNetWebAssets>
              </PropertyGroup>
              {{(browser ? "" : "<ItemGroup><PackageReference Include=\"Microsoft.AspNetCore.Components.WebAssembly.Server\" Version=\"10.0.13\" /></ItemGroup>")}}
              {{fixture.Imports}}
            </Project>
            """);
        string program = browser ? """
            using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
            using PANiXiDA.Core.Observability.WebAssembly;
            var builder = WebAssemblyHostBuilder.CreateDefault(args);
            builder.AddObservability();
            var host = builder.Build();
            host.UseObservability();
            await host.RunAsync();
            """ : """
            using PANiXiDA.Core.Observability;
            var builder = WebApplication.CreateBuilder(args);
            builder.AddObservability();
            var app = builder.Build();
            app.MapStaticAssets();
            app.Run();
            """;
        await File.WriteAllTextAsync(Path.Combine(browserDirectory, "Program.cs"), program, TestContext.Current.CancellationToken);

        var (exitCode, output) = await SdkPackageFixture.RunAsync(fixture.RootDirectory,
            ["build", projectPath, "--nologo", "-m:1", "-nr:false"]);

        exitCode.ShouldBe(0, output);

        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(browserDirectory, "obj", "Debug", "net10.0", "staticwebassets.build.json"),
            TestContext.Current.CancellationToken));
        var assets = manifest.RootElement.GetProperty("Assets").EnumerateArray()
            .Where(asset => asset.GetProperty("SourceId").GetString() == "PANiXiDA.Core.Observability"
                && asset.GetProperty("AssetRole").GetString() == "Primary").ToArray();
        assets.Length.ShouldBe(2);
        assets.ShouldContain(asset => asset.GetProperty("AssetTraitValue").GetString() == "JSLibraryModule");
    }
    [Theory(DisplayName = "SDK should reject resolved versions below its supported stable dependency version")]
    [InlineData("1.18.0", false)]
    [InlineData("1.19.1-beta.1", false)]
    [InlineData("1.19.1", true)]
    [InlineData("1.20.0-beta.1", true)]
    public async Task SdkPackage_Should_ValidateResolvedVersion_When_DependencyIsOverridden(string version, bool accepted)
    {
        var projectPath = fixture.WriteProject($"version-{version}.csproj", $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              {{fixture.Imports}}
              <ItemGroup>
                <ResolvedCompileFileDefinitions Include="exporter.dll" NuGetPackageId="OpenTelemetry.Exporter.OpenTelemetryProtocol" NuGetPackageVersion="{{version}}" />
              </ItemGroup>
            </Project>
            """);

        var (exitCode, output) = await SdkPackageFixture.RunAsync(fixture.RootDirectory,
            ["msbuild", projectPath, "-nologo", "-t:ValidatePanixidaObservabilitySdkDependencies"]);

        (exitCode == 0).ShouldBe(accepted, output);
        if (!accepted)
        {
            output.ShouldContain("PANOBS001");
        }
    }

    [Fact(DisplayName = "SDK should explain migration when imported through an ordinary package reference")]
    public async Task SdkPackage_Should_RejectOrdinaryPackageReference_When_LegacyImportIsUsed()
    {
        var legacyImport = SecurityElement.Escape(Path.Combine(fixture.PackageDirectory, "buildTransitive", "PANiXiDA.Core.Observability.targets"));
        var projectPath = fixture.WriteProject("legacy.proj", $"<Project><Import Project=\"{legacyImport}\" /></Project>");

        var (exitCode, output) = await SdkPackageFixture.RunAsync(fixture.RootDirectory,
            ["msbuild", projectPath, "-nologo", "-t:RequirePanixidaObservabilitySdk"]);

        exitCode.ShouldNotBe(0);
        output.ShouldContain("PANOBS002");
        output.ShouldContain("Remove the ordinary PackageReference");
    }
}

public sealed class SdkPackageFixture : IAsyncLifetime
{
    public string RootDirectory { get; } = Path.Combine(Path.GetTempPath(), "panixida-observability-sdk-" + Guid.NewGuid().ToString("N"));
    public string PackageDirectory => Path.Combine(RootDirectory, "package");
    public string Imports => $"<Import Project=\"{SecurityElement.Escape(Path.Combine(PackageDirectory, "Sdk", "Sdk.props"))}\" /><Import Project=\"{SecurityElement.Escape(Path.Combine(PackageDirectory, "Sdk", "Sdk.targets"))}\" />";

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(RootDirectory);
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "PANiXiDA.Core.Observability.slnx")))
        {
            repository = repository.Parent;
        }

        var projectPath = Path.Combine(repository!.FullName, "src", "PANiXiDA.Core.Observability", "PANiXiDA.Core.Observability.csproj");
        var feed = Path.Combine(RootDirectory, "feed");
        var configuration = typeof(SdkPackageFixture).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        var (exitCode, output) = await RunAsync(repository.FullName,
            ["pack", projectPath, "-c", configuration, "--no-build", "--no-restore", "--nologo", "-o", feed]);
        exitCode.ShouldBe(0, output);
        ZipFile.ExtractToDirectory(Directory.GetFiles(feed, "*.nupkg").Single(), PackageDirectory);
    }

    public ValueTask DisposeAsync()
    {
        Path.GetDirectoryName(Path.GetFullPath(RootDirectory)).ShouldBe(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())));
        Directory.Delete(RootDirectory, recursive: true);
        return ValueTask.CompletedTask;
    }

    public string WriteProject(string name, string source)
    {
        var filename = Path.Combine(RootDirectory, name);
        File.WriteAllText(filename, source);
        return filename;
    }

    public static async Task<(int ExitCode, string Output)> RunAsync(string workingDirectory, string[] arguments)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        return (process.ExitCode, await output + await error);
    }
}
