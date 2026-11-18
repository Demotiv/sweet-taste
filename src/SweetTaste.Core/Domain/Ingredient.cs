namespace SweetTaste.Core.Domain;

/// <summary>Карточка товара. Одна на всё время, покупки хранятся отдельно.</summary>
public sealed record Ingredient(
    Guid Id,
    string Name,
    string? Brand,
    string Unit,
    Guid? CategoryId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? DeletedAt);
