namespace SweetTaste.Core.Domain;

/// <summary>Рецепт: состав на партию, выход и наценка.</summary>
public sealed record Recipe(
    Guid Id,
    string Name,
    Guid? CategoryId,
    decimal OutputQuantity,
    string OutputUnit,
    int MarkupPercent,
    IReadOnlyList<RecipeLine> Ingredients,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? DeletedAt,
    string? Technology,
    byte[]? Photo);

/// <summary>Строка состава: ингредиент и количество на партию.</summary>
public sealed record RecipeLine(
    Guid IngredientId,
    decimal Amount,
    int Position);
