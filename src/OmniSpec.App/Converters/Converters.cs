using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using OmniSpec.Core.Metrics;
using OmniSpec.Core.Storage;

namespace OmniSpec.App.Converters;

internal static class Res
{
    public static Brush Brush(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
}

/// <summary>Available values use the normal text color; anything else is muted (or amber for Permission Required).</summary>
public sealed class StateToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        MetricState.Available => Res.Brush("Brush.Text"),
        MetricState.PermissionRequired => Res.Brush("Brush.Warning"),
        MetricState.Error => Res.Brush("Brush.Critical"),
        _ => Res.Brush("Brush.TextMuted"),
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class HealthToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var soft = parameter as string == "soft";
        return value switch
        {
            DriveHealthLevel.Healthy => Res.Brush(soft ? "Brush.GoodSoft" : "Brush.Good"),
            DriveHealthLevel.Good => Res.Brush(soft ? "Brush.InfoSoft" : "Brush.Info"),
            DriveHealthLevel.Warning => Res.Brush(soft ? "Brush.WarningSoft" : "Brush.Warning"),
            DriveHealthLevel.Critical => Res.Brush(soft ? "Brush.CriticalSoft" : "Brush.Critical"),
            FindingSeverity.Good => Res.Brush("Brush.Good"),
            FindingSeverity.Warning => Res.Brush("Brush.Warning"),
            FindingSeverity.Critical => Res.Brush("Brush.Critical"),
            FindingSeverity.Info => Res.Brush("Brush.Info"),
            _ => Res.Brush(soft ? "Brush.SurfaceHover" : "Brush.TextMuted"),
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Maps a 0–100 load/usage value to a brush: accent below 75, warning below 90, critical above.</summary>
public sealed class LoadToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var v = value is double d ? d : double.NaN;
        if (double.IsNaN(v)) return Res.Brush("Brush.Track");
        return v >= 90 ? Res.Brush("Brush.Critical") : v >= 75 ? Res.Brush("Brush.Warning") : Res.Brush("Brush.Accent");
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var b = value switch
        {
            bool x => x,
            null => false,
            string s => !string.IsNullOrEmpty(s),
            int i => i > 0,
            System.Collections.ICollection c => c.Count > 0,
            _ => true,
        };
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is Visibility.Visible ^ Invert;
}

public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true
            ? targetType.IsEnum && Enum.TryParse(targetType, parameter?.ToString(), out var e) ? e! : parameter!
            : Binding.DoNothing;
}

/// <summary>Displays an <see cref="OmniSpec.Core.SystemInfo.InfoItem"/> with sensitive values hidden.</summary>
public sealed class InfoValueConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is OmniSpec.Core.SystemInfo.InfoItem i ? i.DisplayValue(false) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>NaN-safe double to percentage width/value converter for progress bars.</summary>
public sealed class NanToZeroConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double d && double.IsFinite(d) ? d : 0.0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
