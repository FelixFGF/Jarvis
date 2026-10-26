using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Jarvis.Services;

/// <summary>Returns the value shown by a closed ComboBox without reusing its item container.</summary>
public sealed class ComboBoxSelectionConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ComboBoxItem item ? item.Content : value;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}
