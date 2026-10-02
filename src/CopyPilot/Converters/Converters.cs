using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using CopyPilot.ViewModels;

namespace CopyPilot.Converters;

/// <summary>true → Collapsed, false → Visible.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Empty string → Collapsed.</summary>
public sealed class EmptyToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Colors log lines by kind.</summary>
public sealed class LogKindToBrushConverter : IValueConverter
{
    private static readonly Brush Info = Freeze(new SolidColorBrush(Color.FromRgb(0xF3, 0xED, 0xF7)));
    private static readonly Brush Warning = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0xC2, 0x6B)));
    private static readonly Brush Error = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x7B, 0x72)));
    private static readonly Brush Command = Freeze(new SolidColorBrush(Color.FromRgb(0xC9, 0xA6, 0xDE)));
    private static readonly Brush Progress = Freeze(new SolidColorBrush(Color.FromRgb(0xF3, 0xE0, 0x4F)));
    private static readonly Brush Success = Freeze(new SolidColorBrush(Color.FromRgb(0x7E, 0xE7, 0x87)));

    private static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        LogKind.Warning => Warning,
        LogKind.Error => Error,
        LogKind.Command => Command,
        LogKind.Progress => Progress,
        LogKind.Success => Success,
        _ => Info,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
