namespace SweetTaste.Core.Domain;

/// <summary>
/// Единицы, в которых задаётся выход рецепта: штуки или вес.
/// Вес можно задавать в килограммах (2,5 кг) или граммах (350 г).
/// </summary>
public static class RecipeUnits
{
    public const string Pieces = "шт";
    public const string Kilograms = "кг";
    public const string Grams = "г";

    public static IReadOnlyList<string> Allowed { get; } = new[] { Pieces, Kilograms, Grams };

    public static bool IsAllowed(string? unit) =>
        !string.IsNullOrWhiteSpace(unit) && Allowed.Contains(unit.Trim(), StringComparer.Ordinal);

    public static string Normalize(string? unit) => (unit ?? string.Empty).Trim();

    /// <summary>Вес измеряется дробно, штуки — только целым числом.</summary>
    public static bool IsWeight(string? unit) =>
        string.Equals(Normalize(unit), Kilograms, StringComparison.Ordinal) ||
        string.Equals(Normalize(unit), Grams, StringComparison.Ordinal);

    /// <summary>
    /// Цена за грамм округляется до копейки, за штуку и килограмм — до рубля.
    /// Иначе дешёвые весовые позиции завышались бы в разы.
    /// </summary>
    public static decimal RoundPricePerUnit(decimal value, string? unit)
    {
        var factor = string.Equals(Normalize(unit), Grams, StringComparison.Ordinal) ? 100m : 1m;
        return Math.Ceiling(value * factor) / factor;
    }
}
