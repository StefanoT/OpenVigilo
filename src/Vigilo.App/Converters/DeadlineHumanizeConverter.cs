using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Humanizer;

namespace Vigilo.App.Converters;

public sealed class DeadlineHumanizeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            DateTimeOffset deadline => deadline.Humanize(DateTimeOffset.Now, culture),
            DateTime deadline => deadline.Humanize(dateToCompareAgainst: DateTime.Now, culture: culture),
            null => "No deadline",
            _ => value
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return System.Windows.Data.Binding.DoNothing;
    }
}
