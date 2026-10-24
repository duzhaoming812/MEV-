using System;
using System.Globalization;
using System.Windows.Data;

namespace MEVJiaolongControlCenter.Converters;

public class IntEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int intVal && parameter is string paramStr && int.TryParse(paramStr, out var paramVal))
        {
            return intVal == paramVal;
        }
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool isChecked && isChecked && parameter is string paramStr && int.TryParse(paramStr, out var paramVal))
        {
            return paramVal;
        }
        return Binding.DoNothing;
    }
}
