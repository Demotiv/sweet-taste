namespace SweetTaste.Core.Domain;

/// <summary>Заказ клиента: состав из рецептов, срок выдачи и статус.</summary>
public sealed record Order(
    Guid Id,
    int Number,
    string FirstName,
    string? LastName,
    string Phone,
    Guid? CustomerId,
    DateTimeOffset CreatedAt,
    DateOnly DueDate,
    OrderStatus Status,
    string? Comment,
    IReadOnlyList<OrderItem> Items,
    IReadOnlyList<OrderPackaging> Packaging,
    decimal TotalPrice,
    decimal DiscountAmount,
    decimal PaidAmount,
    DateTimeOffset UpdatedAt)
{
    public string ClientName => string.IsNullOrWhiteSpace(LastName)
        ? FirstName
        : $"{FirstName} {LastName}";

    /// <summary>Себестоимость всего количества и упаковки по ценам на момент оформления.</summary>
    public decimal TotalCost =>
        Items.Sum(item => item.CostPrice * item.Quantity) +
        Packaging.Sum(item => item.CostTotal);

    /// <summary>Есть ли строки с приблизительной себестоимостью (заказы до v6).</summary>
    public bool CostEstimated => Items.Any(item => item.CostEstimated);

    /// <summary>Стоимость упаковки в заказе, уже с наценкой.</summary>
    public decimal PackagingTotal => Packaging.Sum(item => item.SaleTotal);

    /// <summary>Сколько ещё осталось доплатить.</summary>
    public decimal RemainingAmount => Math.Max(0m, TotalPrice - PaidAmount);

    public bool IsPaid => TotalPrice <= 0m || PaidAmount >= TotalPrice;
}

/// <summary>Строка заказа с ценой и себестоимостью на момент оформления.</summary>
public sealed record OrderItem(
    Guid Id,
    Guid? RecipeId,
    string RecipeName,
    decimal Quantity,
    decimal UnitPrice,
    decimal CostPrice,
    decimal OutputQuantity,
    string OutputUnit,
    bool CostEstimated);

/// <summary>Агрегированная закупка под заказ.</summary>
public sealed record ShoppingListItem(
    string IngredientName,
    string Unit,
    decimal Amount,
    decimal EstimatedCost);

/// <summary>Ингредиенты для одной позиции заказа, посчитанные по её количеству.</summary>
public sealed record OrderItemShopping(
    OrderItem Item,
    IReadOnlyList<ShoppingListItem> Lines,
    decimal Cost);

/// <summary>Заказ и список покупок: по каждой позиции отдельно и общий итог.</summary>
public sealed record OrderDetails(
    Order Order,
    IReadOnlyList<OrderItemShopping> ItemShopping,
    IReadOnlyList<ShoppingListItem> ShoppingList,
    decimal ShoppingCost);
