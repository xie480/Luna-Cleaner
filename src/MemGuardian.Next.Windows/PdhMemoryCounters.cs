using MemGuardian.Next.Windows.Native;

namespace MemGuardian.Next.Windows;

/// <summary>Collects language-neutral paging and pagefile counters through one reusable PDH query.</summary>
internal sealed class PdhMemoryCounters : IDisposable
{
    private readonly SafePdhQueryHandle? _query;
    private readonly IntPtr _pageReads;
    private readonly IntPtr _pagesInput;
    private readonly IntPtr _pagefileUsage;

    /// <summary>Opens a query and adds counters independently so one missing counter does not disable all.</summary>
    internal PdhMemoryCounters(ICollection<string> errors)
    {
        if (NativeMethods.PdhOpenQueryW(null, 0, out var query) != NativeConstants.ErrorSuccess)
        {
            errors.Add("PDH query unavailable; paging counters will be shown as unavailable.");
            return;
        }

        _query = new SafePdhQueryHandle(query);
        _pageReads = Add(NativeConstants.PageReadsCounter, errors);
        _pagesInput = Add(NativeConstants.PagesInputCounter, errors);
        _pagefileUsage = Add(NativeConstants.PagefileUsageCounter, errors);
    }

    /// <summary>Collects one PDH query sample and returns null for counters without valid data.</summary>
    internal PdhValues Collect()
    {
        if (_query is null || _query.IsInvalid || NativeMethods.PdhCollectQueryData(_query) != NativeConstants.ErrorSuccess)
            return new PdhValues(null, null, null);
        return new PdhValues(Read(_pageReads), Read(_pagesInput), Read(_pagefileUsage));
    }

    /// <summary>Closes the query, which releases all counter handles and provider resources.</summary>
    public void Dispose() => _query?.Dispose();

    private IntPtr Add(string path, ICollection<string> errors)
    {
        var status = NativeMethods.PdhAddEnglishCounterW(_query!, path, 0, out var counter);
        if (status == NativeConstants.ErrorSuccess) return counter;
        errors.Add($"PDH counter unavailable ({path}, 0x{status:X8}).");
        return IntPtr.Zero;
    }

    private static double? Read(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return null;
        var result = NativeMethods.PdhGetFormattedCounterValue(counter, NativeConstants.PdhFormatDouble,
            out _, out var value);
        if (result != NativeConstants.ErrorSuccess ||
            value.Status is not (NativeConstants.PdhStatusValidData or NativeConstants.PdhStatusNewData) ||
            !double.IsFinite(value.DoubleValue)) return null;
        return value.DoubleValue;
    }
}

/// <summary>Optional PDH sample values; null means unavailable or not yet primed.</summary>
internal readonly record struct PdhValues(double? PageReadsPerSecond, double? PagesInputPerSecond,
    double? PagefileUsagePercent);
