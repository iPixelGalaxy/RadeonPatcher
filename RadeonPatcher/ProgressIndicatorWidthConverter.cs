using System.Globalization;
using System.Windows.Data;

namespace RadeonPatcher;

public sealed class ProgressIndicatorWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length != 5 ||
            values[0] is not double value ||
            values[1] is not double minimum ||
            values[2] is not double maximum ||
            values[3] is not double availableWidth ||
            maximum <= minimum ||
            availableWidth <= 0)
        {
            return 0d;
        }

        return values[4] is true
            ? 42d
            : Math.Clamp((value - minimum) / (maximum - minimum), 0d, 1d) * availableWidth;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
