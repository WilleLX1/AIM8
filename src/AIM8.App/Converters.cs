using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace AIM8.App;

/// <summary>Maps the state keys used across the dashboard onto the palette.</summary>
public sealed class StateBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Success = new(Color.FromRgb(0x3F, 0xB9, 0x50));
    private static readonly SolidColorBrush Running = new(Color.FromRgb(0x58, 0xA6, 0xFF));
    private static readonly SolidColorBrush Failure = new(Color.FromRgb(0xF8, 0x51, 0x49));
    private static readonly SolidColorBrush Warning = new(Color.FromRgb(0xD2, 0x99, 0x22));
    private static readonly SolidColorBrush Faint = new(Color.FromRgb(0x5C, 0x63, 0x73));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value as string) switch
        {
            "success" => Success,
            "running" => Running,
            "failure" => Failure,
            "cancelled" => Warning,
            "skipped" => Faint,
            _ => Faint,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>true -&gt; check, false -&gt; cross, null -&gt; question mark.</summary>
public sealed class TriStateGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        true => "✓",
        false => "✗",
        _ => "?",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class TriStateBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Yes = new(Color.FromRgb(0x3F, 0xB9, 0x50));
    private static readonly SolidColorBrush No = new(Color.FromRgb(0xF8, 0x51, 0x49));
    private static readonly SolidColorBrush Unknown = new(Color.FromRgb(0x5C, 0x63, 0x73));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        true => Yes,
        false => No,
        _ => Unknown,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Collapses an element when its bound string is empty. Pass "invert" to flip it.</summary>
public sealed class StringVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasText = !string.IsNullOrWhiteSpace(value as string);
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase)) hasText = !hasText;
        return hasText ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Bool to visibility, with "invert" support - the built-in one cannot invert.</summary>
public sealed class BoolVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase)) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>For "auto" checkboxes that disable the slider they override.</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}