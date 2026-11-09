using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using OmniSpec.Core.Live;
using OmniSpec.Core.Metrics;
using OmniSpec.Core.SystemInfo;
using OmniSpec.Monitoring;

namespace OmniSpec.App.ViewModels;

/// <summary>Base class for navigable pages. Only the active page receives live updates.</summary>
public abstract class PageViewModel : ObservableObject
{
    public abstract string Title { get; }
    /// <summary>Segoe Fluent Icons glyph.</summary>
    public abstract string Icon { get; }

    public virtual void OnActivated() { }
    public virtual void OnDeactivated() { }
    public virtual void OnLive(LiveSnapshot live) { }
    public virtual void OnDrives(IReadOnlyList<DriveState> drives) { }
    public virtual void OnSystemInfo(SystemInfoReport report) { }
}

/// <summary>A label/value row with its data source and state, updated in place to avoid UI churn.</summary>
public sealed partial class ValueRow : ObservableObject
{
    public ValueRow(string label) => Label = label;

    public string Label { get; }
    [ObservableProperty] private string _value = "—";
    [ObservableProperty] private MetricState _state = MetricState.Unavailable;
    [ObservableProperty] private string _source = string.Empty;
    [ObservableProperty] private string? _note;
    [ObservableProperty] private string? _sub;

    public string ToolTipText => string.IsNullOrEmpty(Note) ? $"Source: {Source}" : $"Source: {Source}\n{Note}";

    partial void OnSourceChanged(string value) => OnPropertyChanged(nameof(ToolTipText));
    partial void OnNoteChanged(string? value) => OnPropertyChanged(nameof(ToolTipText));
}

/// <summary>Keyed collection of <see cref="ValueRow"/> that updates existing rows instead of recreating them.</summary>
public sealed class RowList : ObservableCollection<ValueRow>
{
    private readonly Dictionary<string, ValueRow> _index = new(StringComparer.Ordinal);

    /// <summary>
    /// Adds or updates a row. Rows whose value is not available are removed (and a detached row is returned),
    /// so the UI only ever shows real, readable values.
    /// </summary>
    public ValueRow Set(string label, string value, string source, MetricState state = MetricState.Available, string? note = null)
    {
        if (state != MetricState.Available)
        {
            if (_index.Remove(label, out var stale)) Remove(stale);
            return new ValueRow(label);
        }
        if (!_index.TryGetValue(label, out var row))
        {
            row = new ValueRow(label);
            _index[label] = row;
            Add(row);
        }
        row.Value = value;
        row.State = state;
        row.Source = source;
        row.Note = note;
        return row;
    }

    public ValueRow Set(string label, Metric<double> metric, string format, string unit = "") =>
        Set(label, metric.Format(format, unit), metric.Source, metric.State, metric.Note);

    public ValueRow Set<T>(string label, Metric<T> metric, Func<T, string> format) =>
        Set(label, metric.HasValue ? format(metric.Value!) : MetricText.ForState(metric.State), metric.Source, metric.State, metric.Note);

    public void Reset()
    {
        _index.Clear();
        Clear();
    }
}

public static class Charts
{
    public static System.Windows.Media.Brush Brush(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.SteelBlue;

    public static Controls.ChartSeries Series(Core.History.MetricHistory history, string key, string name, string brushKey = "Brush.Accent") =>
        new(name, history.Get(key), Brush(brushKey));

    public static IReadOnlyList<Controls.ChartSeries> One(Core.History.MetricHistory history, string key, string name, string brushKey = "Brush.Accent") =>
        [Series(history, key, name, brushKey)];
}

/// <summary>Formatting helpers for the UI. Unavailable values are rendered as their state, never as zero.</summary>
public static class Fmt
{
    public static string Bytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        var v = Math.Abs(bytes);
        var u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        var sign = bytes < 0 ? "−" : "";
        return u == 0 ? $"{sign}{v:0} {units[u]}" : $"{sign}{v:0.##} {units[u]}";
    }

    public static string Rate(double bytesPerSecond) => Bytes(bytesPerSecond) + "/s";

    public static string Bits(double bitsPerSecond) =>
        bitsPerSecond >= 1e9 ? $"{bitsPerSecond / 1e9:0.##} Gbps" :
        bitsPerSecond >= 1e6 ? $"{bitsPerSecond / 1e6:0.#} Mbps" :
        bitsPerSecond >= 1e3 ? $"{bitsPerSecond / 1e3:0.#} Kbps" : $"{bitsPerSecond:0} bps";

    public static string Mb(double mb) => mb >= 1024 ? $"{mb / 1024:0.0} GB" : $"{mb:0} MB";

    public static string Temp(Metric<double>? m) => m is null ? "—" : m.HasValue ? $"{m.Value:0} °C" : "—";
    public static string Pct(Metric<double>? m) => m is null ? "—" : m.HasValue ? $"{m.Value:0}%" : "—";
    public static string Num(double? v, string format = "0") => v is double d && double.IsFinite(d) ? d.ToString(format, CultureInfo.CurrentCulture) : "—";

    public static string Duration(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m" :
        t.TotalHours >= 1 ? $"{t.Hours}h {t.Minutes}m" :
        t.TotalMinutes >= 1 ? $"{t.Minutes}m {t.Seconds}s" : $"{t.Seconds}s";

    public static double Value(Metric<double>? m) => m is { HasValue: true } ? m.Value : double.NaN;

    /// <summary>Min/Max/Avg text for a history key, e.g. "min 34 · max 71 · avg 48 °C".</summary>
    public static string Stats((double Min, double Max, double Avg, int Count) s, string format, string unit) =>
        s.Count == 0 ? "No samples yet" :
        $"min {s.Min.ToString(format, CultureInfo.CurrentCulture)} · max {s.Max.ToString(format, CultureInfo.CurrentCulture)} · avg {s.Avg.ToString(format, CultureInfo.CurrentCulture)}{(unit.Length > 0 ? " " + unit : "")}";
}
