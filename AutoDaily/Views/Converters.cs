using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace AutoDaily.Views;

/// <summary>"#RRGGBB" → 筆刷；ConverterParameter 可指定透明度（0~1）。</summary>
public sealed class HexBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        Color color;
        try
        {
            color = (Color)ColorConverter.ConvertFromString(value as string ?? "#7C6CFF");
        }
        catch
        {
            color = Color.FromRgb(0x7C, 0x6C, 0xFF);
        }

        if (parameter is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var alpha))
            color.A = (byte)Math.Clamp(alpha * 255, 0, 255);

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class EmptyToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
