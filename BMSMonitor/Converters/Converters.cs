using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using BMSMonitor.Models;
using Windows.UI;

namespace BMSMonitor.Converters;

public class CellStateToBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is CellState state)
        {
            return state switch
            {
                CellState.Low => new SolidColorBrush(Color.FromArgb(50, 255, 140, 0)),
                CellState.Undervoltage => new SolidColorBrush(Color.FromArgb(50, 220, 53, 69)),
                CellState.Overvoltage => new SolidColorBrush(Color.FromArgb(50, 111, 66, 193)),
                _ => new SolidColorBrush(Colors.Transparent)
            };
        }
        return new SolidColorBrush(Colors.Transparent);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

public class CellStateToBorderConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is CellState state)
        {
            return state switch
            {
                CellState.Low => new SolidColorBrush(Color.FromArgb(180, 255, 140, 0)),
                CellState.Undervoltage => new SolidColorBrush(Color.FromArgb(180, 220, 53, 69)),
                CellState.Overvoltage => new SolidColorBrush(Color.FromArgb(180, 111, 66, 193)),
                _ => new SolidColorBrush(Color.FromArgb(30, 128, 128, 128))
            };
        }
        return new SolidColorBrush(Color.FromArgb(30, 128, 128, 128));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool boolValue = value is bool b && b;
        bool invert = parameter is string s && s == "Invert";
        return (boolValue ^ invert) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

public class TempToForegroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is double temp)
        {
            if (temp >= 70) return new SolidColorBrush(Color.FromArgb(255, 220, 53, 69));
            if (temp >= 60) return new SolidColorBrush(Color.FromArgb(255, 255, 140, 0));
        }
        return new SolidColorBrush(Colors.Transparent);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

public class StatusToForegroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is string status)
        {
            return status.ToLower() switch
            {
                "charging" => new SolidColorBrush(Color.FromArgb(255, 37, 198, 133)),
                "discharging" => new SolidColorBrush(Color.FromArgb(255, 0, 120, 212)),
                "idle" => new SolidColorBrush(Color.FromArgb(255, 128, 128, 128)),
                "fault" => new SolidColorBrush(Color.FromArgb(255, 220, 53, 69)),
                _ => new SolidColorBrush(Color.FromArgb(255, 128, 128, 128))
            };
        }
        return new SolidColorBrush(Color.FromArgb(255, 128, 128, 128));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
