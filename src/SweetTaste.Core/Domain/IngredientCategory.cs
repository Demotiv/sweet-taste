namespace SweetTaste.Core.Domain;

/// <summary>Категория справочника ингредиентов: молочное, бакалея и т. п.</summary>
public sealed record IngredientCategory(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAt);
