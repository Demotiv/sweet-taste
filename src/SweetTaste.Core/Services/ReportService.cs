using SweetTaste.Core.Domain;

namespace SweetTaste.Core.Services;

/// <summary>
/// Отчёты за период. Период определяется датой выдачи заказа;
/// выручка и прибыль считаются только по выданным заказам.
/// </summary>
public sealed class ReportService
{
    private const int TopItemsCount = 10;

    private readonly OrderService _orders;
    private readonly InventoryService _inventory;
    private readonly CustomerService? _customers;

    public ReportService(
        OrderService orders,
        InventoryService inventory,
        CustomerService? customers = null)
    {
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _customers = customers;
    }

    public Report Build(DateOnly from, DateOnly to)
    {
        if (to < from)
        {
            throw new ValidationException("Конец периода раньше начала.");
        }

        var orders = _orders.GetOrders();
        var issued = orders
            .Where(order => order.Status == OrderStatus.Done && IsInPeriod(order.DueDate))
            .ToList();

        var inProgress = orders
            .Where(order => order.Status is OrderStatus.New or OrderStatus.Cooking or OrderStatus.Ready &&
                            IsInPeriod(order.DueDate))
            .ToList();

        var purchases = _inventory.GetPurchases(from, to);

        var revenue = issued.Sum(order => order.TotalPrice);
        var cost = issued.Sum(order => order.TotalCost);

        var topItems = issued
            .SelectMany(order => order.Items)
            .GroupBy(item => (item.RecipeName, item.OutputUnit))
            .Select(group =>
            {
                var quantity = group.Sum(item => item.Quantity);
                var groupRevenue = group.Sum(item => item.UnitPrice * item.Quantity);
                var groupCost = group.Sum(item => item.CostPrice * item.Quantity);
                return new ReportTopItem(
                    group.First().RecipeName,
                    group.Key.OutputUnit,
                    quantity,
                    groupRevenue,
                    groupCost,
                    groupRevenue - groupCost);
            })
            .OrderByDescending(item => item.Quantity)
            .ThenByDescending(item => item.Revenue)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(TopItemsCount)
            .ToList();

        var customers = BuildCustomers(issued);

        var firstOrders = orders
            .Where(order => order.CustomerId is not null)
            .GroupBy(order => order.CustomerId!.Value)
            .ToDictionary(group => group.Key, group => group.Min(order => order.CreatedAt));

        var newCustomers = customers.Count(customer =>
            firstOrders.TryGetValue(customer.CustomerId, out var first) &&
            IsInPeriod(DateOnly.FromDateTime(first.Date)));

        var days = issued
            .GroupBy(order => order.DueDate)
            .OrderBy(group => group.Key)
            .Select(group => new ReportDay(
                group.Key,
                group.Count(),
                group.Sum(order => order.TotalPrice),
                group.Sum(order => order.TotalCost)))
            .ToList();

        return new Report(
            from,
            to,
            issued.Count,
            revenue,
            cost,
            revenue - cost,
            issued.Count == 0 ? 0m : revenue / issued.Count,
            purchases.Count,
            purchases.Sum(purchase => purchase.TotalPrice),
            inProgress.Count,
            inProgress.Sum(order => order.TotalPrice),
            issued.Any(order => order.CostEstimated),
            customers.Count,
            newCustomers,
            customers,
            topItems,
            days);

        bool IsInPeriod(DateOnly date) => date >= from && date <= to;
    }

    private List<ReportCustomer> BuildCustomers(IReadOnlyList<Order> issued)
    {
        var catalog = _customers?.GetCustomers().ToDictionary(customer => customer.Id)
                      ?? new Dictionary<Guid, Customer>();

        return issued
            .Where(order => order.CustomerId is not null)
            .GroupBy(order => order.CustomerId!.Value)
            .Select(group =>
            {
                var last = group
                    .OrderByDescending(order => order.DueDate)
                    .ThenByDescending(order => order.CreatedAt)
                    .First();

                var revenue = group.Sum(order => order.TotalPrice);
                var count = group.Count();
                var profile = catalog.GetValueOrDefault(group.Key);

                return new ReportCustomer(
                    group.Key,
                    profile?.Number ?? 0,
                    profile?.FullName ?? last.ClientName,
                    profile?.Phone ?? last.Phone,
                    count,
                    revenue,
                    count == 0 ? 0m : revenue / count,
                    group.Max(order => order.DueDate));
            })
            .OrderByDescending(customer => customer.OrdersCount)
            .ThenByDescending(customer => customer.Revenue)
            .ThenBy(customer => customer.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
