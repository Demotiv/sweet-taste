namespace SweetTaste.Core.Domain;

/// <summary>
/// Отчёт за период по дате выдачи. Выручка и прибыль считаются только по выданным заказам;
/// себестоимость берётся из снимка, сделанного при оформлении заказа.
/// </summary>
public sealed record Report(
    DateOnly From,
    DateOnly To,
    int OrdersCount,
    decimal Revenue,
    decimal Cost,
    decimal Profit,
    decimal AverageCheck,
    int PurchasesCount,
    decimal PurchasesCost,
    int InProgressCount,
    decimal InProgressRevenue,
    bool HasEstimatedCost,
    int CustomersCount,
    int NewCustomersCount,
    IReadOnlyList<ReportCustomer> Customers,
    IReadOnlyList<ReportTopItem> TopItems,
    IReadOnlyList<ReportDay> Days)
{
    /// <summary>В периоде нет ни выданных заказов, ни покупок.</summary>
    public bool IsEmpty => OrdersCount == 0 && PurchasesCount == 0;
}

/// <summary>Клиент в отчёте: сколько заказов и выручки принёс за период.</summary>
public sealed record ReportCustomer(
    Guid CustomerId,
    int Number,
    string Name,
    string Phone,
    int OrdersCount,
    decimal Revenue,
    decimal AverageCheck,
    DateOnly LastOrderDate);

/// <summary>Позиция отчёта: сколько продано и что это принесло.</summary>
public sealed record ReportTopItem(
    string Name,
    string Unit,
    decimal Quantity,
    decimal Revenue,
    decimal Cost,
    decimal Profit);

/// <summary>Итоги одного дня периода.</summary>
public sealed record ReportDay(
    DateOnly Date,
    int OrdersCount,
    decimal Revenue,
    decimal Cost);
