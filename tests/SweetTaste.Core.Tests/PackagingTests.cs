using SweetTaste.Core.Domain;
using SweetTaste.Core.Services;

namespace SweetTaste.Core.Tests;

public class PackagingTests
{
    private static (TestDatabase Db, RecipeService Recipes, OrderService Orders, PackagingService Packaging,
        Guid Cake, Guid Cookie) CreateBase()
    {
        var db = new TestDatabase();
        var flour = db.Service.AddIngredient("Мука", null, Units.Grams);
        var sugar = db.Service.AddIngredient("Сахар", null, Units.Grams);
        var butter = db.Service.AddIngredient("Масло", null, Units.Grams);

        var today = DateOnly.FromDateTime(DateTime.Now);
        db.Service.AddPurchase(flour.Id, today, "Магнит", 1000m, 100m);   // 0.10 ₽/г
        db.Service.AddPurchase(sugar.Id, today, "Магнит", 1000m, 200m);   // 0.20 ₽/г
        db.Service.AddPurchase(butter.Id, today, "Магнит", 1000m, 1000m); // 1.00 ₽/г

        var recipes = new RecipeService(db.Database, db.Service);
        var cake = recipes.CreateRecipe("Кекс", null, 10, RecipeUnits.Pieces, 100,
            new[] { (flour.Id, 500m), (sugar.Id, 200m), (butter.Id, 100m) });
        var cookie = recipes.CreateRecipe("Печенье", null, 5, RecipeUnits.Pieces, 50,
            new[] { (flour.Id, 100m), (sugar.Id, 50m) });

        var orders = new OrderService(db.Database, recipes);
        var packaging = new PackagingService(db.Database);
        return (db, recipes, orders, packaging, cake.Id, cookie.Id);
    }

    private static DateOnly Tomorrow => DateOnly.FromDateTime(DateTime.Now).AddDays(1);

    private static void Issue(OrderService orders, Guid orderId)
    {
        orders.AdvanceOrder(orderId);
        orders.AdvanceOrder(orderId);
        orders.AdvanceOrder(orderId);
    }

    [Fact]
    public void Catalog_ManagesCategoriesAndItems()
    {
        var (db, recipes, orders, packaging, cake, cookie) = CreateBase();
        using var _ = db;

        var box = packaging.CreateItem("Коробка 20×20", "20×20×10 см", 150m, "Торты");
        var item = packaging.GetItems().Single();

        Assert.Equal("Коробка 20×20", item.Item.Name);
        Assert.Equal("20×20×10 см", item.Item.Size);
        Assert.Equal(150m, item.Item.Price);
        Assert.Equal("Торты", item.CategoryName);

        var updated = packaging.UpdateItem(box.Id, "Коробка 25×25", "25×25×12 см", 180m, "Бенто");
        Assert.Equal(180m, updated.Price);
        Assert.Equal("Бенто", packaging.GetItems().Single().CategoryName);

        packaging.DeleteCategory("Бенто");
        Assert.Null(packaging.GetItems().Single().CategoryName);

        packaging.DeleteItem(box.Id);
        Assert.Empty(packaging.GetItems());
    }

    [Fact]
    public void PackagingCategory_ReservedForNoCategory_IsRejected()
    {
        var (db, _, _, packaging, _, _) = CreateBase();
        using var _db = db;

        Assert.Throws<ValidationException>(() => packaging.CreateCategory("Без категории"));
        Assert.Throws<ValidationException>(() =>
            packaging.CreateItem("Коробка", "20×20", 100m, "Без категории"));
    }

    [Fact]
    public void SetOrderPackaging_AddsWeightedMarkupAndQuantity()
    {
        var (db, recipes, orders, packaging, cake, cookie) = CreateBase();
        using var _ = db;

        var box = packaging.CreateItem("Коробка", "20×20", 100m, "Торты");
        var order = orders.CreateOrder("Иван", null, "1", Tomorrow, null, new[] { (cake, 2m) });

        var updated = orders.SetOrderPackaging(order.Id, new[] { (box.Id, 2) });

        var row = Assert.Single(updated.Packaging);
        Assert.Equal(200m, row.SalePrice); // наценка заказа 100% (38 / 19)
        Assert.Equal(400m, updated.PackagingTotal);
        Assert.Equal(476m, updated.TotalPrice); // 76 за кексы + 400 за коробки
        Assert.Equal(238m, updated.TotalCost);  // 38 + 200

        var reloaded = orders.GetOrders().Single(item => item.Id == order.Id);
        Assert.Equal(476m, reloaded.TotalPrice);
        Assert.Equal(2, reloaded.Packaging.Single().Quantity);
        Assert.Equal(200m, reloaded.Packaging.Single().SalePrice);
    }

    [Fact]
    public void SetOrderPackaging_UsesWeightedAverageMarkup()
    {
        var (db, recipes, orders, packaging, cake, cookie) = CreateBase();
        using var _ = db;

        var box = packaging.CreateItem("Коробка", null, 100m, null);
        var order = orders.CreateOrder("Иван", null, "1", Tomorrow, null,
            new[] { (cake, 1m), (cookie, 1m) });

        // кекс: 38 ₽ при себестоимости 19; печенье: 6 ₽ при себестоимости 4.
        var updated = orders.SetOrderPackaging(order.Id, new[] { (box.Id, 1) });
        var factor = 44m / 23m;

        Assert.Equal(44m, updated.Items.Sum(item => item.UnitPrice * item.Quantity));
        Assert.Equal(191.30m, updated.PackagingTotal);
        Assert.Equal(236m, updated.TotalPrice); // 235,30 → вверх до рубля
        Assert.Equal(Math.Round(100m * factor, 2, MidpointRounding.AwayFromZero),
            updated.Packaging.Single().SalePrice);
    }

    [Fact]
    public void SetOrderPackaging_MergesDuplicateItems()
    {
        var (db, recipes, orders, packaging, cake, cookie) = CreateBase();
        using var _ = db;

        var box = packaging.CreateItem("Коробка", null, 50m, null);
        var order = orders.CreateOrder("Иван", null, "1", Tomorrow, null, new[] { (cake, 1m) });

        var updated = orders.SetOrderPackaging(order.Id, new[] { (box.Id, 1), (box.Id, 2) });

        Assert.Equal(3, Assert.Single(updated.Packaging).Quantity);
        Assert.Equal(300m, updated.PackagingTotal); // 50 × 2 наценка × 3
    }

    [Fact]
    public void UpdateOrder_RecalculatesPackaging()
    {
        var (db, recipes, orders, packaging, cake, cookie) = CreateBase();
        using var _ = db;

        var box = packaging.CreateItem("Коробка", null, 100m, null);
        var order = orders.CreateOrder("Иван", null, "1", Tomorrow, null, new[] { (cake, 2m) });
        orders.SetOrderPackaging(order.Id, new[] { (box.Id, 2) });

        var updated = orders.UpdateOrder(order.Id, "Иван", null, "1", Tomorrow, null, new[] { (cake, 1m) });

        Assert.Equal(438m, updated.TotalPrice); // 38 за кекс + 200 × 2 за коробки
        Assert.Equal(200m, updated.Packaging.Single().SalePrice);
        Assert.Equal(2, updated.Packaging.Single().Quantity);
    }

    [Fact]
    public void SetOrderPackaging_RejectsFinishedOrder()
    {
        var (db, recipes, orders, packaging, cake, cookie) = CreateBase();
        using var _ = db;

        var box = packaging.CreateItem("Коробка", null, 100m, null);
        var order = orders.CreateOrder("Иван", null, "1", Tomorrow, null, new[] { (cake, 1m) });
        Issue(orders, order.Id);

        Assert.Throws<ValidationException>(() =>
            orders.SetOrderPackaging(order.Id, new[] { (box.Id, 1) }));
    }

    [Fact]
    public void DeletedPackaging_KeepsOrderSnapshot()
    {
        var (db, recipes, orders, packaging, cake, cookie) = CreateBase();
        using var _ = db;

        var box = packaging.CreateItem("Коробка", "20×20", 100m, "Торты");
        var order = orders.CreateOrder("Иван", null, "1", Tomorrow, null, new[] { (cake, 2m) });
        orders.SetOrderPackaging(order.Id, new[] { (box.Id, 1) });

        packaging.DeleteItem(box.Id);

        var reloaded = orders.GetOrders().Single(item => item.Id == order.Id);
        var row = Assert.Single(reloaded.Packaging);
        Assert.Equal("Коробка", row.Name);
        Assert.Equal("20×20", row.Size);
        Assert.Null(row.PackagingId);
        Assert.Equal(276m, reloaded.TotalPrice);
    }

    [Fact]
    public void Report_IncludesPackagingInRevenueAndCost()
    {
        var (db, recipes, orders, packaging, cake, cookie) = CreateBase();
        using var _ = db;

        var box = packaging.CreateItem("Коробка", null, 100m, null);
        var order = orders.CreateOrder("Иван", null, "1", Tomorrow, null, new[] { (cake, 2m) });
        orders.SetOrderPackaging(order.Id, new[] { (box.Id, 2) });
        Issue(orders, order.Id);

        var today = DateOnly.FromDateTime(DateTime.Now);
        var report = new ReportService(orders, db.Service).Build(today, today.AddDays(7));

        Assert.Equal(476m, report.Revenue);
        Assert.Equal(238m, report.Cost);
        Assert.Equal(238m, report.Profit);
    }
}
