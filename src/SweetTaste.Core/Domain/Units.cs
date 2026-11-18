namespace SweetTaste.Core.Domain;

/// <summary>Базовые единицы, в которых хранятся количества.</summary>
public static class Units
{
    public const string Grams = "г";
    public const string Milliliters = "мл";
    public const string Pieces = "шт";

    public static IReadOnlyList<string> Allowed { get; } = new[] { Grams, Milliliters, Pieces };

    public static bool IsAllowed(string? unit) =>
        !string.IsNullOrWhiteSpace(unit) && Allowed.Contains(unit.Trim(), StringComparer.Ordinal);

    public static string Normalize(string? unit) => (unit ?? string.Empty).Trim();

    public static string FormatQuantity(decimal quantity, string unit)
    {
        var value = quantity.ToString("0.###", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
        return value + " " + unit;
    }
}
