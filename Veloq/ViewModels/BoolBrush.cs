using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Veloq.ViewModels;

public sealed class BoolBrush : IValueConverter
{
    private readonly IBrush _true;
    private readonly IBrush _false;

    private BoolBrush(Color onTrue, Color onFalse)
    {
        _true = new SolidColorBrush(onTrue);
        _false = new SolidColorBrush(onFalse);
    }

    public static readonly BoolBrush RedOrAccent = new(Color.Parse("#f85149"), Color.Parse("#2dd4bf"));

    public static readonly BoolBrush RedOrLo = new(Color.Parse("#f85149"), Color.Parse("#8b98a9"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? _true : _false;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
