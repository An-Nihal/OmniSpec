using System.Management;
using OmniSpec.Core.Metrics;

namespace OmniSpec.SystemInformation.Wmi;

/// <summary>Result of a WMI query, including a state so callers can distinguish "empty" from "access denied".</summary>
public sealed record WmiResult(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, MetricState State, string? Error)
{
    public bool Ok => State == MetricState.Available;
}

/// <summary>Small, defensive WMI helper with timeouts. All exceptions are converted into states.</summary>
public static class WmiQuery
{
    public const string CimV2 = @"root\cimv2";
    public const string Storage = @"root\Microsoft\Windows\Storage";
    public const string RootWmi = @"root\wmi";
    public const string Power = @"root\cimv2\power";

    public static WmiResult Query(string wql, string scope = CimV2, TimeSpan? timeout = null)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        try
        {
            var options = new System.Management.EnumerationOptions { Timeout = timeout ?? TimeSpan.FromSeconds(15), ReturnImmediately = true, Rewindable = false };
            using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(wql), options);
            using var results = searcher.Get();
            foreach (var obj in results)
            {
                using (obj)
                {
                    rows.Add(ToDictionary(obj));
                }
            }
            return new WmiResult(rows, MetricState.Available, null);
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
        {
            return new WmiResult(rows, MetricState.PermissionRequired, "Access denied (administrator rights required)");
        }
        catch (ManagementException ex) when (ex.ErrorCode is ManagementStatus.InvalidClass or ManagementStatus.InvalidNamespace or ManagementStatus.NotSupported)
        {
            return new WmiResult(rows, MetricState.Unsupported, ex.Message.Trim());
        }
        catch (UnauthorizedAccessException ex)
        {
            return new WmiResult(rows, MetricState.PermissionRequired, ex.Message);
        }
        catch (Exception ex)
        {
            return new WmiResult(rows, MetricState.Error, ex.Message.Trim());
        }
    }

    public static IReadOnlyDictionary<string, object?> ToDictionary(ManagementBaseObject obj)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in obj.Properties) dict[p.Name] = p.Value;
        return dict;
    }

    public static string? Str(this IReadOnlyDictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var v) || v is null) return null;
        var s = v switch
        {
            string str => str,
            string[] arr => string.Join(", ", arr),
            _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture),
        };
        s = s?.Trim().TrimEnd('\0');
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    public static ulong? U64(this IReadOnlyDictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var v) || v is null) return null;
        try { return Convert.ToUInt64(v, System.Globalization.CultureInfo.InvariantCulture); }
        catch { return null; }
    }

    public static long? I64(this IReadOnlyDictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var v) || v is null) return null;
        try { return Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture); }
        catch { return null; }
    }

    public static double? Dbl(this IReadOnlyDictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var v) || v is null) return null;
        try { return Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture); }
        catch { return null; }
    }

    public static bool? Bool(this IReadOnlyDictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var v) && v is bool b ? b : null;

    public static DateTime? Date(this IReadOnlyDictionary<string, object?> row, string key)
    {
        var s = row.Str(key);
        if (s is null) return null;
        try { return ManagementDateTimeConverter.ToDateTime(s); }
        catch { return null; }
    }
}
