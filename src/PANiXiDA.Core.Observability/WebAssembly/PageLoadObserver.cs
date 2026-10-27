using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace PANiXiDA.Core.Observability.WebAssembly;

/// <summary>Measures one routed page until its required data and rendered content are ready.</summary>
public sealed class PageLoadObserver : ComponentBase, IDisposable
{
    private BrowserPerformanceTelemetry? _telemetry;

    /// <summary>Gets or sets whether required page data is still loading.</summary>
    [Parameter]
    public bool IsLoading { get; set; }

    /// <summary>Gets or sets whether the page has rendered a failed loading result.</summary>
    [Parameter]
    public bool HasError { get; set; }

    [Inject]
    private IServiceProvider Services { get; set; } = null!;

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        _telemetry = Services.GetService<BrowserPerformanceTelemetry>();
        _telemetry?.PageObservationRequested += OnPageObservationRequested;
    }

    /// <inheritdoc />
    protected override void OnAfterRender(bool firstRender)
    {
        _telemetry?.PageRendered(this, IsLoading, HasError);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _telemetry?.PageObservationRequested -= OnPageObservationRequested;
        _telemetry?.PageRemoved(this);
    }

    private void OnPageObservationRequested()
    {
        _ = InvokeAsync(StateHasChanged);
    }
}
