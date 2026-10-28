using System.Diagnostics.Metrics;

using OpenTelemetry.Metrics;

namespace PANiXiDA.Core.Observability.WebAssembly;

internal sealed class BrowserRuntimeMetrics : IDisposable
{
    internal const string SourceName = "PANiXiDA.Core.Observability.Browser.Runtime";
    private static readonly HashSet<string> UnsupportedMetrics = new(StringComparer.Ordinal)
    {
        "dotnet.process.memory.working_set",
        "dotnet.gc.collections",
        "dotnet.gc.last_collection.heap.size",
        "dotnet.gc.last_collection.heap.fragmentation.size",
        "dotnet.gc.pause.time",
        "dotnet.jit.compiled_il.size",
        "dotnet.jit.compiled_methods",
        "dotnet.jit.compilation.time",
        "dotnet.thread_pool.work_item.count"
    };
    private readonly Meter _meter = new(SourceName);

    public BrowserRuntimeMetrics()
    {
        _meter.CreateObservableCounter("dotnet.gc.collections", GetCollectionCounts, "{collection}");
        _meter.CreateObservableGauge("browser.runtime.gc.heap.size", () => (double)GC.GetTotalMemory(false), "By");
        _meter.CreateObservableGauge("browser.runtime.gc.last_collection.heap.size",
            () => (double)GC.GetGCMemoryInfo().HeapSizeBytes, "By");
        _meter.CreateObservableGauge("browser.runtime.gc.last_collection.heap.fragmentation.size",
            () => (double)GC.GetGCMemoryInfo().FragmentedBytes, "By");
    }

    internal static MetricStreamConfiguration? ConfigureInstrument(Instrument instrument)
    {
        return instrument.Meter.Name == "System.Runtime" && UnsupportedMetrics.Contains(instrument.Name)
            ? MetricStreamConfiguration.Drop
            : null;
    }

    private static IEnumerable<Measurement<long>> GetCollectionCounts()
    {
        for (int generation = 0; generation <= GC.MaxGeneration; generation++)
        {
            yield return new Measurement<long>(GC.CollectionCount(generation),
                new KeyValuePair<string, object?>("gc.heap.generation", "gen" + generation));
        }
    }

    public void Dispose()
    {
        _meter.Dispose();
    }
}
