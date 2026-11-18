namespace SweetTaste.Core.Domain;

/// <summary>Строка справочника с посчитанной средней ценой.</summary>
public sealed record IngredientSummary(
    Ingredient Ingredient,
    string? CategoryName,
    decimal? AveragePricePerUnit,
    int PurchaseCount,
    decimal TotalQuantity,
    decimal TotalSpent,
    Purchase? LastPurchase);
