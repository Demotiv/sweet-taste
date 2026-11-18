namespace SweetTaste.Core.Domain;

/// <summary>Категория упаковки: для тортов, бенто, макарон и т.п.</summary>
public sealed record PackagingCategory(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAt);

/// <summary>Коробка или пакет: название, размер и закупочная цена.</summary>
public sealed record PackagingItem(
    Guid Id,
    Guid? CategoryId,
    string Name,
    string? Size,
    decimal Price,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Упаковка вместе с названием категории для списков.</summary>
public sealed record PackagingSummary(
    PackagingItem Item,
    string? CategoryName);

/// <summary>Снимок упаковки в заказе: цена закупки и цена с наценкой на момент выбора.</summary>
public sealed record OrderPackaging(
    Guid Id,
    Guid? PackagingId,
    string Name,
    string? Size,
    decimal CostPrice,
    decimal SalePrice,
    int Quantity)
{
    public decimal CostTotal => CostPrice * Quantity;

    public decimal SaleTotal => SalePrice * Quantity;
}
