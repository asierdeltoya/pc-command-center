using System.Diagnostics;
using PCCommandCenter.Core;

namespace PCCommandCenter.Hardware.Counters;

/// <summary>
/// Reads every instance of a performance-counter category in a single call (ReadCategory) and computes
/// cooked values against the previous sample. Much cheaper than one PerformanceCounter per instance.
/// </summary>
internal sealed class CategorySampler
{
    private readonly string _categoryName;
    private readonly string[] _counters;
    private PerformanceCounterCategory? _category;
    private Dictionary<string, Dictionary<string, CounterSample>> _previous = new();

    public CategorySampler(string category, params string[] counters)
    {
        _categoryName = category;
        _counters = counters;
        try
        {
            if (PerformanceCounterCategory.Exists(category)) _category = new PerformanceCounterCategory(category);
            else AppLog.Info($"Performance counter category '{category}' is not present on this system");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Unable to open counter category '{category}'", ex);
        }
    }

    public bool IsAvailable => _category != null;

    /// <summary>Returns counter → instance → cooked value. Empty when the category is unavailable.</summary>
    public Dictionary<string, Dictionary<string, double>> Sample()
    {
        var result = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
        if (!IsAvailable) return result;
        try
        {
            var data = _category!.ReadCategory();
            var current = new Dictionary<string, Dictionary<string, CounterSample>>(StringComparer.OrdinalIgnoreCase);
            foreach (var counter in _counters)
            {
                var collection = data[counter];
                if (collection == null) continue;
                var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                var samples = new Dictionary<string, CounterSample>(StringComparer.OrdinalIgnoreCase);
                _previous.TryGetValue(counter, out var prevSamples);
                foreach (InstanceData instance in collection.Values)
                {
                    var sample = instance.Sample;
                    samples[instance.InstanceName] = sample;
                    try
                    {
                        if (prevSamples != null && prevSamples.TryGetValue(instance.InstanceName, out var prev))
                            values[instance.InstanceName] = CounterSample.Calculate(prev, sample);
                        else if (IsInstantaneous(sample.CounterType))
                            values[instance.InstanceName] = CounterSample.Calculate(sample);
                    }
                    catch { /* counter wrapped or instance restarted; skip this tick */ }
                }
                current[counter] = samples;
                result[counter] = values;
            }
            _previous = current;
        }
        catch (Exception ex)
        {
            // Some categories disappear (e.g. GPU driver reset). Retry silently on the next tick.
            AppLog.Debug($"Reading '{_categoryName}' failed: {ex.Message}");
        }
        return result;
    }

    private static bool IsInstantaneous(PerformanceCounterType type) => type is
        PerformanceCounterType.NumberOfItems32 or PerformanceCounterType.NumberOfItems64 or
        PerformanceCounterType.NumberOfItemsHEX32 or PerformanceCounterType.NumberOfItemsHEX64 or
        PerformanceCounterType.RawFraction;
}

internal static class SampleExtensions
{
    public static double? Get(this Dictionary<string, Dictionary<string, double>> data, string counter, string instance) =>
        data.TryGetValue(counter, out var inst) && inst.TryGetValue(instance, out var v) && !double.IsNaN(v) ? v : null;
}
