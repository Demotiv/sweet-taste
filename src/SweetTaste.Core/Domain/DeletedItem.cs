namespace SweetTaste.Core.Domain;

/// <summary>Удалённый ингредиент в корзине.</summary>
public sealed record DeletedIngredient(
    Ingredient Ingredient,
    string? CategoryName,
    DateTimeOffset DeletedAt,
    int PurchaseCount);

/// <summary>Удалённый рецепт в корзине.</summary>
public sealed record DeletedRecipe(
    Recipe Recipe,
    string? CategoryName,
    DateTimeOffset DeletedAt);
