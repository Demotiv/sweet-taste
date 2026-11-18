using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace SweetTaste.Desktop;

/// <summary>
/// Ввод чисел в NumericUpDown: принимает и «2,5», и «2.5», показывает с запятой.
/// NumericUpDown использует Convert как «текст → число», а ConvertBack — как «число → текст».
/// </summary>
public sealed class DecimalInputConverter : IValueConverter
{
    public static DecimalInputConverter Instance { get; } = new();

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string text)
        {
            return null;
        }

        var normalized = text.Trim().Replace(" ", string.Empty).Replace('.', ',');
        if (normalized.Length == 0)
        {
            return null;
        }

        return decimal.TryParse(normalized, NumberStyles.Number, Ru, out var number)
            ? number
            : AvaloniaProperty.UnsetValue;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is decimal number ? number.ToString("0.###", Ru) : null;
}
