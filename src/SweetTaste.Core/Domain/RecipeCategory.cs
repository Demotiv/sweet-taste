namespace SweetTaste.Core.Domain;

/// <summary>Категория книги рецептов. Кондитер создаёт их сам.</summary>
public sealed record RecipeCategory(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAt);
