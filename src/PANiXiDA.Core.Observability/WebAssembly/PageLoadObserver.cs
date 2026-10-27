using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace PANiXiDA.Core.Observability.WebAssembly;

/// <summary>Measures one routed page until its required data and rendered content are ready.</summary>
public sealed class PageLoadObserver : ComponentBase, IDisposable
{
    private BrowserPerformanceTelemetry? _telemetry;
    private long _renderVersion;
    private bool _disposed;

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
        if (_telemetry is not null)
        {
            _telemetry.RouteMatched += OnRouteMatched;
        }
    }

    /// <inheritdoc />
    protected override void OnAfterRender(bool firstRender)
    {
        _renderVersion++;
        _telemetry?.PageRendered(this, IsLoading, HasError);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        if (_telemetry is not null)
        {
            _telemetry.RouteMatched -= OnRouteMatched;
            _telemetry.PageRemoved(this);
        }
    }

    private void OnRouteMatched()
    {
        long version = _renderVersion;
        _ = InvokeAsync(async () =>
        {
            await Task.Yield();
            if (!_disposed && _renderVersion == version)
            {
                StateHasChanged();
            }
        });
    }
}
