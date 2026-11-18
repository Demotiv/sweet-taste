namespace SweetTaste.Core.Domain;

/// <summary>Одна закупка ингредиента: когда, где, сколько и за какую сумму.</summary>
public sealed record Purchase(
    Guid Id,
    Guid IngredientId,
    DateOnly PurchasedOn,
    string? Store,
    decimal Quantity,
    decimal TotalPrice,
    Guid? OrderId,
    string? Note,
    DateTimeOffset CreatedAt)
{
    /// <summary>Цена за единицу в этой конкретной закупке.</summary>
    public decimal PricePerUnit => Quantity > 0m ? TotalPrice / Quantity : 0m;
}
