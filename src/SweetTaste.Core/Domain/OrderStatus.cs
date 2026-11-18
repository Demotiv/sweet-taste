namespace SweetTaste.Core.Domain;

public enum OrderStatus
{
    New = 0,
    Cooking = 1,
    Ready = 2,
    Done = 3,
    Cancelled = 4,
}

public static class OrderStatuses
{
    public static bool IsFinal(OrderStatus status) => status is OrderStatus.Done or OrderStatus.Cancelled;

    public static OrderStatus Advance(OrderStatus status) => status switch
    {
        OrderStatus.New => OrderStatus.Cooking,
        OrderStatus.Cooking => OrderStatus.Ready,
        OrderStatus.Ready => OrderStatus.Done,
        _ => throw new InvalidOperationException("Заказ уже закрыт."),
    };
}
