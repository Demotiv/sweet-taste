using SweetTaste.Core.Domain;
using SweetTaste.Core.Services;
using SweetTaste.Core.Storage;

namespace SweetTaste.Core.Tests;

public class OrderTests
{
    private static (TestDatabase Db, RecipeService Recipes, OrderService Orders, Guid Cake, Guid Cookie) CreateBase()
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
        var cake = recipes.CreateRecipe("Кекс", "Выпечка", 10, RecipeUnits.Pieces, 100,
            new[] { (flour.Id, 500m), (sugar.Id, 200m), (butter.Id, 100m) });
        var cookie = recipes.CreateRecipe("Печенье", null, 5, RecipeUnits.Pieces, 100,
            new[] { (flour.Id, 100m), (sugar.Id, 50m) });

        var orders = new OrderService(db.Database, recipes);
        return (db, recipes, orders, cake.Id, cookie.Id);
    }

    private static DateOnly Tomorrow => DateOnly.FromDateTime(DateTime.Now).AddDays(1);

    private static void Issue(OrderService orders, Guid orderId)
    {
        orders.AdvanceOrder(orderId);
        orders.AdvanceOrder(orderId);
        orders.AdvanceOrder(orderId);
    }

    [Fact]
    public void CreateOrder_SnapshotsPricesAndNumbersOrders()
    {
        var (db, recipes, orders, cake, cookie) = CreateBase();
        using var _ = db;

        var first = orders.CreateOrder("Иван", "Петров", "+7 900 000-00-00", Tomorrow, "к 18:00",
            new[] { (cake, 2m), (cookie, 3m) });
        var second = orders.CreateOrder("Анна", null, "123", Tomorrow, null,
            new[] { (cake, 1m) });

        Assert.Equal(1, first.Number);
        Assert.Equal(2, second.Number);
        Assert.Equal(OrderStatus.New, first.Status);
        Assert.Equal(100m, first.TotalPrice);
        Assert.Equal(2, first.Items.Count);
        Assert.Equal(38m, first.Items.Single(item => item.RecipeId == cake).UnitPrice);

        recipes.SetRecipeMarkup(cake, 200);

        Assert.Equal(100m, orders.GetOrders().Single(order => order.Id == first.Id).TotalPrice);
        Assert.Equal(38m, orders.GetOrders().Single(order => order.Id == second.Id).Items.Single().UnitPrice);

        var third = orders.CreateOrder("Мария", null, "789", Tomorrow, null, new[] { (cake, 1m) });
        Assert.Equal(57m, third.Items.Single().UnitPrice);
    }

    [Fact]
    public void CreateOrder_ValidatesClientDateAndLines()
    {
        var (db, recipes, orders, cake, cookie) = CreateBase();
        using var _ = db;

        Assert.Throws<ValidationException>(() =>
            orders.CreateOrder("", null, "123", Tomorrow, null, new[] { (cake, 1m) }));

        Assert.Throws<ValidationException>(() =>
            orders.CreateOrder("Иван", null, "", Tomorrow, null, new[] { (cake, 1m) }));

        Assert.Throws<ValidationException>(() =>
            orders.CreateOrder("Иван", null, "123", DateOnly.FromDateTime(DateTime.Now).AddDays(-1), null,
                new[] { (cake, 1m) }));

        Assert.Throws<ValidationException>(() =>
            orders.CreateOrder("Иван", null, "123", Tomorrow, null, Array.Empty<(Guid, decimal)>()));

        Assert.Throws<ValidationException>(() =>
            orders.CreateOrder("Иван", null, "123", Tomorrow, null, new[] { (Guid.NewGuid(), 1m) }));
    }

    [Fact]
    public void AdvanceOrder_WalksThroughStatusesUntilDone()
    {
        var (db, recipes, orders, cake, cookie) = CreateBase();
        using var _ = db;
        var order = orders.CreateOrder("Иван", null, "123", Tomorrow, null, new[] { (cake, 1m) });

        Assert.Equal(OrderStatus.Cooking, orders.AdvanceOrder(order.Id));
        Assert.Equal(OrderStatus.Ready, orders.AdvanceOrder(order.Id));
        Assert.Equal(OrderStatus.Done, orders.AdvanceOrder(order.Id));
        Assert.Throws<ValidationException>(() => orders.AdvanceOrder(order.Id));
        Assert.Throws<ValidationException>(() => orders.CancelOrder(order.Id));
    }

    [Fact]
    public void CancelOrder_WorksOnlyForActiveOrders()
    {
        var (db, recipes, orders, cake, cookie) = CreateBase();
        using var _ = db;
        var order = orders.CreateOrder("Иван", null, "123", Tomorrow, null, new[] { (cake, 1m) });

        orders.CancelOrder(order.Id);
        Assert.Equal(OrderStatus.Cancelled, orders.GetOrders().Single().Status);

        Assert.Throws<ValidationException>(() => orders.CancelOrder(order.Id));
        Assert.Throws<ValidationException>(() => orders.AdvanceOrder(order.Id));
    }

    [Fact]
    public void SetOrderStatus_AllowsBackwardAndReturnFromArchive()
    {
        var (db, recipes, orders, cake, cookie) = CreateBase();
        using var _ = db;
        var order = orders.CreateOrder("Иван", null, "123", Tomorrow, null, new[] { (cake, 1m) });

        Issue(orders, order.Id);
        Assert.Equal(OrderStatus.Done, orders.GetOrders().Single().Status);

        Assert.Equal(OrderStatus.Ready, orders.SetOrderStatus(order.Id, OrderStatus.Ready));
        Assert.Equal(OrderStatus.Ready, orders.GetOrders().Single().Status);

        orders.SetOrderStatus(order.Id, OrderStatus.New);
        Assert.Equal(OrderStatus.New, orders.GetOrders().Single().Status);

        orders.SetOrderStatus(order.Id, OrderStatus.Cancelled);
        orders.SetOrderStatus(order.Id, OrderStatus.Cooking);
        Assert.Equal(OrderStatus.Cooking, orders.GetOrders().Single().Status);

        Assert.Throws<ValidationException>(() =>
            orders.SetOrderStatus(order.Id, (OrderStatus)99));
    }

    [Fact]
    public void UpdateOrder_RecalculatesTotalAndChangesClient()
    {
        var (db, recipes, orders, cake, cookie) = CreateBase();
        using var _ = db;
        var order = orders.CreateOrder("Иван", null, "123", Tomorrow, null, new[] { (cake, 2m) });

        var updated = orders.UpdateOrder(order.Id, "Пётр", "Сидоров", "456", Tomorrow.AddDays(1),
            "без орехов", new[] { (cake, 1m), (cookie, 1m) });

        Assert.Equal("Пётр", updated.FirstName);
        Assert.Equal("Сидоров", updated.LastName);
        Assert.Equal("456", updated.Phone);
        Assert.Equal(46m, updated.TotalPrice);
        Assert.Equal(2, updated.Items.Count);

        orders.AdvanceOrder(order.Id);
        orders.AdvanceOrder(order.Id);
        orders.AdvanceOrder(order.Id);

        Assert.Throws<ValidationException>(() =>
            orders.UpdateOrder(order.Id, "Иван", null, "123", Tomorrow, null, new[] { (cake, 1m) }));
    }

    [Fact]
    public void DeleteArchivedOrders_RemovesOnlyFinishedOrders()
    {
        var (db, recipes, orders, cake, cookie) = CreateBase();
        using var _ = db;
        var flourId = db.Service.GetIngredients().Single(item => item.Ingredient.Name == "Мука").Ingredient.Id;

        var done = orders.CreateOrder("Иван", null, "1", Tomorrow, null, new[] { (cake, 1m) });
        var cancelled = orders.CreateOrder("Анна", null, "2", Tomorrow, null, new[] { (cookie, 1m) });
        var active = orders.CreateOrder("Пётр", null, "3", Tomorrow, null, new[] { (cake, 1m) });

        Issue(orders, done.Id);
        orders.CancelOrder(cancelled.Id);
        db.Service.AddPurchase(flourId, DateOnly.FromDateTime(DateTime.Now), null, 100m, 10m, done.Id);

        var deleted = orders.DeleteArchivedOrders();

        Assert.Equal(2, deleted);
        var remaining = Assert.Single(orders.GetOrders());
        Assert.Equal(active.Id, remaining.Id);
        var purchase = db.Service.GetDetails(flourId).Purchases.Single(item => item.Quantity == 100m);
        Assert.Null(purchase.OrderId);
    }

    [Fact]
    public void DeleteAllOrders_ClearsHistoryButKeepsCatalog()
    {
        var (db, recipes, orders, cake, cookie) = CreateBase();
        using var _ = db;

        orders.CreateOrder("Иван", null, "1", Tomorrow, null, new[] { (cake, 1m) });
        var second = orders.CreateOrder("Анна", null, "2", Tomorrow, null, new[] { (cookie, 1m) });
        Issue(orders, second.Id);

        var deleted = orders.DeleteAllOrders();

        Assert.Equal(2, deleted);
        Assert.Empty(orders.GetOrders());
        Assert.Equal(2, recipes.GetRecipes().Count);
        Assert.Equal(3, db.Service.GetIngredients().Count);
    }

    [Fact]
    public void Payment_TracksPrepaymentAndRemaining()
    {
        var (db, recipes, orders, cake, cookie) = CreateBase();
        using var _ = db;

        var order = orders.CreateOrder("Иван", null, "123", Tomorrow, null,
            new[] { (cake, 2m) }, paidAmount: 30m);

        Assert.Equal(30m, order.PaidAmount);
        Assert.Equal(46m, order.RemainingAmount);
        Assert.False(order.IsPaid);
        Assert.Equal(30m, orders.GetOrders().Single(item => item.Id == order.Id).PaidAmount);

        var paid = orders.MarkOrderPaid(order.Id);
        Assert.Equal(76m, paid.PaidAmount);
        Assert.Equal(0m, paid.RemainingAmount);
        Assert.True(paid.IsPaid);

        Assert.Throws<ValidationException>(() =>
            orders.CreateOrder("Иван", null, "123", Tomorrow, null,
                new[] { (cake, 1m) }, paidAmount: 100m));

        Assert.Throws<ValidationException>(() =>
            orders.SetOrderPaid(order.Id, -1m));
    }

    [Fact]
    public void Payment_ClampsWhenOrderBecomesCheaper()
    {
        var (db, recipes, orders, cake, cookie) = CreateBase();
        using var _ = db;

        var order = orders.CreateOrder("Иван", null, "123", Tomorrow, null,
            new[] { (cake, 2m) }, paidAmount: 50m);

        var cheaper = orders.UpdateOrder(order.Id, "Иван", null, "123", Tomorrow, null,
            new[] { (cake, 1m) });

        Assert.Equal(38m, cheaper.TotalPrice);
        Assert.Equal(38m, cheaper.PaidAmount);
        Assert.True(cheaper.IsPaid);

        var partial = orders.UpdateOrder(order.Id, "Иван", null, "123", Tomorrow, null,
            new[] { (cake, 1m) }, paidAmount: 10m);
        Assert.Equal(10m, partial.PaidAmount);
        Assert.Equal(28m, partial.RemainingAmount);
    }

    [Fact]
    public void Discount_ReducesOrderTotalAndIsStored()
    {
        var (db, recipes, orders, cake, cookie) = CreateBase();
        using var _ = db;

        var order = orders.CreateOrder("Иван", null, "123", Tomorrow, null,
            new[] { (cake, 2m) }, paidAmount: 60m, discountAmount: 16m);

        Assert.Equal(60m, order.TotalPrice);
        Assert.Equal(16m, order.DiscountAmount);
        Assert.True(order.IsPaid);

        var reloaded = orders.GetOrders().Single(item => item.Id == order.Id);
        Assert.Equal(16m, reloaded.DiscountAmount);
        Assert.Equal(60m, reloaded.TotalPrice);

        Assert.Throws<ValidationException>(() =>
            orders.CreateOrder("Иван", null, "123", Tomorrow, null,
                new[] { (cake, 1m) }, discountAmount: 100m));

        var rounded = orders.CreateOrder("Иван", null, "123", Tomorrow, null,
            new[] { (cake, 1m) }, discountAmount: 0.5m);

        Assert.Equal(38m, rounded.TotalPrice); // 38 − 0,5 = 37,5 → вверх до 38
    }

    [Fact]
    public void ShoppingList_AggregatesAndScalesIngredients()
    {
        var (db, recipes, orders, cake, cookie) = CreateBase();
        using var _ = db;
        var order = orders.CreateOrder("Иван", null, "123", Tomorrow, null,
            new[] { (cake, 2m), (cookie, 3m) });

        var details = orders.GetOrderDetails(order.Id);
        var flour = details.ShoppingList.Single(item => item.IngredientName == "Мука");

        Assert.Equal(160m, flour.Amount);
        Assert.Equal(16m, flour.EstimatedCost);
        Assert.Equal(50m, details.ShoppingCost);
        Assert.Equal(3, details.ShoppingList.Count);

        Assert.Equal(2, details.ItemShopping.Count);

        var cakeShopping = details.ItemShopping.Single(group => group.Item.RecipeName == "Кекс");
        Assert.Equal(38m, cakeShopping.Cost);
        Assert.Equal(100m, cakeShopping.Lines.Single(line => line.IngredientName == "Мука").Amount);

        var cookieShopping = details.ItemShopping.Single(group => group.Item.RecipeName == "Печенье");
        Assert.Equal(12m, cookieShopping.Cost);
        Assert.Equal(60m, cookieShopping.Lines.Single(line => line.IngredientName == "Мука").Amount);
    }

    [Fact]
    public void OrdersSurviveReopen()
    {
        var (db, recipes, orders, cake, cookie) = CreateBase();
        using var _ = db;
        var order = orders.CreateOrder("Иван", "Петров", "123", Tomorrow, "комментарий",
            new[] { (cake, 2m) });

        var inventory = db.Reopen();
        var reopenedRecipes = new RecipeService(new AppDatabase(db.Database.FilePath), inventory);
        var reopened = new OrderService(new AppDatabase(db.Database.FilePath), reopenedRecipes);
        var loaded = reopened.GetOrders().Single();

        Assert.Equal(order.Number, loaded.Number);
        Assert.Equal("Иван Петров", loaded.ClientName);
        Assert.Equal(76m, loaded.TotalPrice);
        Assert.Equal("комментарий", loaded.Comment);
    }

    [Fact]
    public void TrashedRecipe_DoesNotBreakExistingOrder()
    {
        var (db, recipes, orders, cake, cookie) = CreateBase();
        using var _ = db;
        var order = orders.CreateOrder("Иван", null, "123", Tomorrow, null, new[] { (cake, 1m) });

        recipes.MoveRecipeToTrash(cake);

        var loaded = orders.GetOrders().Single();
        Assert.Equal("Кекс", loaded.Items.Single().RecipeName);
        Assert.Equal(38m, loaded.TotalPrice);

        var details = orders.GetOrderDetails(order.Id);
        Assert.Empty(details.ShoppingList);
    }
}
