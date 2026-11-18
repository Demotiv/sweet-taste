namespace SweetTaste.Core.Domain;

/// <summary>Цифры для верхней панели раздела «Ингредиенты».</summary>
public sealed record InventoryOverview(
    int IngredientCount,
    int PurchaseCount,
    decimal SpentThisMonth,
    int PurchasesThisMonth,
    Purchase? LargestPurchaseThisMonth,
    string? LargestPurchaseIngredientName);
