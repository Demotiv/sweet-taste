namespace SweetTaste.Core.Domain;

/// <summary>Строка списка рецептов с рассчитанной себестоимостью и ценой.</summary>
public sealed record RecipeSummary(
    Recipe Recipe,
    string? CategoryName,
    decimal BatchCost,
    decimal CostPerUnit,
    decimal PricePerUnit,
    int IngredientCount);
