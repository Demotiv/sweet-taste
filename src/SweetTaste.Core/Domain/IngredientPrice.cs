namespace SweetTaste.Core.Domain;

/// <summary>Ингредиент с текущей средней ценой — для формы рецепта.</summary>
public sealed record IngredientPrice(
    Guid Id,
    string Name,
    string Unit,
    string? CategoryName,
    decimal? AveragePricePerUnit);
