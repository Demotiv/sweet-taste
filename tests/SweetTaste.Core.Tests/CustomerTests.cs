using Microsoft.Data.Sqlite;
using SweetTaste.Core.Domain;
using SweetTaste.Core.Services;
using SweetTaste.Core.Storage;

namespace SweetTaste.Core.Tests;

public class CustomerTests
{
    private static (TestDatabase Db, RecipeService Recipes, OrderService Orders, CustomerService Customers,
        Guid Cake) CreateBase()
    {
        var db = new TestDatabase();
        var flour = db.Service.AddIngredient("Мука", null, Units.Grams);
        var sugar = db.Service.AddIngredient("Сахар", null, Units.Grams);

        var today = DateOnly.FromDateTime(DateTime.Now);
        db.Service.AddPurchase(flour.Id, today, "Магнит", 1000m, 100m);
        db.Service.AddPurchase(sugar.Id, today, "Магнит", 1000m, 200m);

        var recipes = new RecipeService(db.Database, db.Service);
        var cake = recipes.CreateRecipe("Кекс", null, 10m, RecipeUnits.Pieces, 100,
            new[] { (flour.Id, 500m), (sugar.Id, 200m) });

        var orders = new OrderService(db.Database, recipes);
        var customers = new CustomerService(db.Database);
        return (db, recipes, orders, customers, cake.Id);
    }

    private static DateOnly Tomorrow => DateOnly.FromDateTime(DateTime.Now).AddDays(1);

    private static void Issue(OrderService orders, Guid orderId)
    {
        orders.AdvanceOrder(orderId);
        orders.AdvanceOrder(orderId);
        orders.AdvanceOrder(orderId);
    }

    [Fact]
    public void Customer_GetsNumberAndDeduplicatesPhone()
    {
        var (db, recipes, orders, customers, cake) = CreateBase();
        using var _ = db;

        var first = customers.ResolveOrCreate("Анна", "Смирнова", "+7 900 111-22-33");
        var second = customers.ResolveOrCreate("Анна", "Смирнова", "8 900 111 22 33");
        var third = customers.ResolveOrCreate("Пётр", null, "+7 900 555-66-77");

        Assert.Equal(1, first.Number);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(2, third.Number);
        Assert.Equal(2, customers.GetCustomers().Count);
    }

    [Fact]
    public void Customer_UpdatesNameAndIsFoundByPhoneOrLastName()
    {
        var (db, recipes, orders, customers, cake) = CreateBase();
        using var _ = db;

        customers.ResolveOrCreate("Анна", "Смирнова", "+7 900 111-22-33");
        var updated = customers.ResolveOrCreate("Анна", "Иванова", "+7 900 111-22-33");

        Assert.Equal("Иванова", updated.LastName);
        Assert.Single(customers.Search("Иванов"));
        Assert.Single(customers.Search("900 111"));
        Assert.Empty(customers.Search("Петров"));
    }

    [Fact]
    public void Orders_ShareCustomerAndKeepSnapshot()
    {
        var (db, recipes, orders, customers, cake) = CreateBase();
        using var _ = db;

        var first = orders.CreateOrder("Анна", "Смирнова", "+7 900 111-22-33", Tomorrow, null,
            new[] { (cake, 1m) });
        var second = orders.CreateOrder("Анна", "Смирнова", "8 900 111 22 33", Tomorrow, null,
            new[] { (cake, 2m) });

        Assert.NotNull(first.CustomerId);
        Assert.Equal(first.CustomerId, second.CustomerId);
        Assert.Single(customers.GetCustomers());

        var reloaded = orders.GetOrders();
        Assert.All(reloaded, order => Assert.Equal(first.CustomerId, order.CustomerId));
    }

    [Fact]
    public void Report_ShowsCustomerStatisticsAndNewClients()
    {
        var (db, recipes, orders, customers, cake) = CreateBase();
        using var _ = db;
        var reports = new ReportService(orders, db.Service, customers);

        var annaFirst = orders.CreateOrder("Анна", "Смирнова", "+7 900 111-22-33", Tomorrow, null,
            new[] { (cake, 1m) });
        var annaSecond = orders.CreateOrder("Анна", "Смирнова", "+7 900 111-22-33", Tomorrow, null,
            new[] { (cake, 2m) });
        var peter = orders.CreateOrder("Пётр", null, "+7 900 555-66-77", Tomorrow, null,
            new[] { (cake, 1m) });

        Issue(orders, annaFirst.Id);
        Issue(orders, annaSecond.Id);
        Issue(orders, peter.Id);

        var today = DateOnly.FromDateTime(DateTime.Now);
        var report = reports.Build(today, today.AddDays(7));

        Assert.Equal(2, report.CustomersCount);
        Assert.Equal(2, report.NewCustomersCount);

        var top = report.Customers[0];
        Assert.Equal("Анна Смирнова", top.Name);
        Assert.Equal(2, top.OrdersCount);
        Assert.Equal(54m, top.Revenue);
        Assert.Equal(27m, top.AverageCheck);
        Assert.Equal(1, top.Number);
    }

    [Fact]
    public void Customer_DeleteKeepsOrdersButUnlinksThem()
    {
        var (db, recipes, orders, customers, cake) = CreateBase();
        using var _ = db;

        var order = orders.CreateOrder("Анна", "Смирнова", "+7 900 111-22-33", Tomorrow, null,
            new[] { (cake, 1m) });
        var customer = customers.GetCustomers().Single();

        var unlinked = customers.DeleteCustomer(customer.Id);

        Assert.Equal(1, unlinked);
        Assert.Empty(customers.GetCustomers());
        var remaining = Assert.Single(orders.GetOrders());
        Assert.Equal(order.Id, remaining.Id);
        Assert.Null(remaining.CustomerId);
        Assert.Equal("Анна Смирнова", remaining.ClientName);

        var again = orders.CreateOrder("Анна", "Смирнова", "+7 900 111-22-33", Tomorrow, null,
            new[] { (cake, 1m) });
        Assert.NotNull(again.CustomerId);
        Assert.Single(customers.GetCustomers());
    }

    [Fact]
    public void Migration_BackfillsCustomersFromOrders()
    {
        var (db, recipes, orders, customers, cake) = CreateBase();
        using var _ = db;

        orders.CreateOrder("Анна", "Смирнова", "+7 900 111-22-33", Tomorrow, null, new[] { (cake, 1m) });
        orders.CreateOrder("Анна", "Смирнова", "8 900 111 22 33", Tomorrow, null, new[] { (cake, 2m) });
        orders.CreateOrder("Пётр", null, "+7 900 555-66-77", Tomorrow, null, new[] { (cake, 1m) });

        using (var connection = db.Database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                DROP INDEX ix_orders_customer;
                ALTER TABLE orders DROP COLUMN discount_kopecks;
                ALTER TABLE orders DROP COLUMN paid_amount_kopecks;
                ALTER TABLE orders DROP COLUMN customer_id;
                ALTER TABLE recipes DROP COLUMN technology;
                ALTER TABLE recipes DROP COLUMN photo;
                DROP TABLE customers;
                PRAGMA user_version = 11;
                """;
            command.ExecuteNonQuery();
        }

        var database = new AppDatabase(db.Database.FilePath);
        var reopenedInventory = new InventoryService(database);
        var reopenedRecipes = new RecipeService(database, reopenedInventory);
        var reopened = new OrderService(database, reopenedRecipes);
        var reopenedCustomers = new CustomerService(database);

        var all = reopened.GetOrders();
        Assert.Equal(3, all.Count);
        Assert.All(all, order => Assert.NotNull(order.CustomerId));
        Assert.Equal(2, reopenedCustomers.GetCustomers().Count);

        var annaOrders = all.Where(order => order.Phone.Contains("111")).ToList();
        Assert.Equal(2, annaOrders.Count);
        Assert.Single(annaOrders.Select(order => order.CustomerId).Distinct());
    }
}
