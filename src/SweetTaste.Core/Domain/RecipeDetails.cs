namespace SweetTaste.Core.Domain;

/// <summary>Рецепт с раскладкой по ингредиентам и ценами.</summary>
public sealed record RecipeDetails(
    Recipe Recipe,
    string? CategoryName,
    IReadOnlyList<RecipeLineDetails> Lines,
    decimal BatchCost,
    decimal CostPerUnit,
    decimal PricePerUnit);

/// <summary>Строка состава с ценой ингредиента и вкладом в себестоимость.</summary>
public sealed record RecipeLineDetails(
    Ingredient Ingredient,
    decimal Amount,
    decimal? AveragePricePerUnit,
    decimal Cost);
