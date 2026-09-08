using System.Globalization;
using System.Windows.Data;
using Humanizer;

namespace Vigilo.App.Converters;

public sealed class EmailDateTimeHumanizeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            DateTimeOffset dateTime => dateTime.Humanize(DateTimeOffset.Now, culture),
            DateTime dateTime => dateTime.Humanize(dateToCompareAgainst: DateTime.Now, culture: culture),
            null => "Unknown",
            _ => value
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        System.Windows.Data.Binding.DoNothing;
}
