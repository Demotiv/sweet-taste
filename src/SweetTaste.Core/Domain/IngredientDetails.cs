namespace SweetTaste.Core.Domain;

/// <summary>Ингредиент со всей историей закупок.</summary>
public sealed record IngredientDetails(
    Ingredient Ingredient,
    string? CategoryName,
    IReadOnlyList<Purchase> Purchases,
    decimal? AveragePricePerUnit,
    decimal TotalQuantity,
    decimal TotalSpent);
