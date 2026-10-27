using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace PANiXiDA.Core.Observability.WebAssembly;

/// <summary>Measures one routed page until its required data and rendered content are ready.</summary>
public sealed class PageLoadObserver : ComponentBase, IDisposable
{
    /// <summary>Gets or sets whether required page data is still loading.</summary>
    [Parameter]
    public bool IsLoading { get; set; }

    /// <summary>Gets or sets whether the page has rendered a failed loading result.</summary>
    [Parameter]
    public bool HasError { get; set; }

    [Inject]
    private IServiceProvider Services { get; set; } = null!;

    /// <inheritdoc />
    protected override void OnAfterRender(bool firstRender)
    {
        Services.GetService<BrowserPerformanceTelemetry>()?.PageRendered(this, IsLoading, HasError);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Services.GetService<BrowserPerformanceTelemetry>()?.PageRemoved(this);
    }
}
