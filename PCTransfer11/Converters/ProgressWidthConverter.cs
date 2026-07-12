using System;
using System.Globalization;
using System.Windows.Data;

namespace PCTransfer11.Converters;

/// <summary>
/// Berekent de pixelbreedte van de gevulde balk van een custom-getemplatete ProgressBar.
/// Verwacht 4 bindingen, in deze volgorde: Value, Minimum, Maximum, ActualWidth (van de track).
/// Dit vervangt een TemplateBinding van Value (double) naar ColumnDefinition.Width (GridLength),
/// wat in WPF onbetrouwbaar is en stilletjes terugvalt op een gelijke (50/50) verdeling.
/// </summary>
public sealed class ProgressWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 4)
            return 0.0;
        if (values[0] is not double value || values[1] is not double min ||
            values[2] is not double max || values[3] is not double totalWidth)
            return 0.0;

        if (double.IsNaN(totalWidth) || totalWidth <= 0 || max <= min)
            return 0.0;

        double fraction = (value - min) / (max - min);
        fraction = Math.Clamp(fraction, 0.0, 1.0);
        return fraction * totalWidth;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
