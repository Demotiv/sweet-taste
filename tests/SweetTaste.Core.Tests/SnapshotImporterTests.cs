using SweetTaste.Core.Domain;
using SweetTaste.Core.Services;
using SweetTaste.Core.Sync;

namespace SweetTaste.Core.Tests;

public class SnapshotImporterTests
{
    private static readonly DateTimeOffset TakenAt = new(2026, 11, 18, 12, 0, 0, TimeSpan.FromHours(3));

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    private static DateOnly Tomorrow => Today.AddDays(1);

    private static SyncSnapshot EmptySnapshot() => new()
    {
        SchemaVersion = 15,
        TakenAt = TakenAt,
        IngredientCategories = Array.Empty<SyncIngredientCategory>(),
        RecipeCategories = Array.Empty<SyncRecipeCategory>(),
        PackagingCategories = Array.Empty<SyncPackagingCategory>(),
        Ingredients = Array.Empty<SyncIngredient>(),
        Purchases = Array.Empty<SyncPurchase>(),
        Recipes = Array.Empty<SyncRecipe>(),
        RecipeIngredients = Array.Empty<SyncRecipeIngredient>(),
        Customers = Array.Empty<SyncCustomer>(),
        Orders = Array.Empty<SyncOrder>(),
        OrderItems = Array.Empty<SyncOrderItem>(),
        PackagingItems = Array.Empty<SyncPackagingItem>(),
        OrderPackaging = Array.Empty<SyncOrderPackaging>(),
    };

    /// <summary>Наполняет базу через сервисы и возвращает снапшот этих данных.</summary>
    private static SyncSnapshot FilledSnapshot(TestDatabase db)
    {
        var flour = db.Service.AddIngredient("Мука", "Макфа", Units.Grams, "Сыпучие");
        db.Service.AddPurchase(flour.Id, Today, "Магнит", 1000m, 100m);

        var recipes = new RecipeService(db.Database, db.Service);
        var cake = recipes.CreateRecipe("Кекс", "Выпечка", 10m, RecipeUnits.Pieces, 100,
            new[] { (flour.Id, 500m) }, "Смешать и запечь.");
        recipes.SetRecipePhoto(cake.Id, new byte[] { 1, 2, 250 });

        var orders = new OrderService(db.Database, recipes);
        var order = orders.CreateOrder("Анна", "Смирнова", "+7 900 111-22-33", Tomorrow, null,
            new[] { (cake.Id, 2m) });

        var packaging = new PackagingService(db.Database);
        var box = packaging.CreateItem("Коробка", "20x20", 50m, "Картон");
        orders.SetOrderPackaging(order.Id, new[] { (box.Id, 1) });

        return new SnapshotExporter(db.Database).Export(TakenAt);
    }

    [Fact]
    public void Import_ReplacesLocalDataWithSnapshot()
    {
        using var source = new TestDatabase();
        var snapshot = FilledSnapshot(source);

        using var target = new TestDatabase();
        var imported = new SnapshotImporter(target.Database).Import(snapshot);

        Assert.Equal(snapshot.TotalRows, imported);

        var ingredients = target.Service.GetIngredients();
        var ingredient = Assert.Single(ingredients);
        Assert.Equal("Мука", ingredient.Ingredient.Name);
        Assert.Equal("Макфа", ingredient.Ingredient.Brand);

        var orders = new OrderService(target.Database, new RecipeService(target.Database, target.Service));
        var order = Assert.Single(orders.GetOrders());
        Assert.Equal("Анна", order.FirstName);
        Assert.Equal(2m, Assert.Single(order.Items).Quantity);
    }

    [Fact]
    public void Import_KeepsPhotoAndTechnology()
    {
        using var source = new TestDatabase();
        var snapshot = FilledSnapshot(source);

        using var target = new TestDatabase();
        new SnapshotImporter(target.Database).Import(snapshot);

        var recipes = new RecipeService(target.Database, target.Service);
        var recipe = Assert.Single(recipes.GetRecipes());

        Assert.Equal("Кекс", recipe.Recipe.Name);
        Assert.Equal("Смешать и запечь.", recipe.Recipe.Technology);
        Assert.Equal(new byte[] { 1, 2, 250 }, recipe.Recipe.Photo);
    }

    [Fact]
    public void Import_ReplacesExistingRows()
    {
        using var source = new TestDatabase();
        var snapshot = FilledSnapshot(source);

        using var target = new TestDatabase();
        target.Service.AddIngredient("Сахар", null, Units.Grams, null);
        target.Service.AddIngredient("Соль", null, Units.Grams, null);

        new SnapshotImporter(target.Database).Import(snapshot);

        var names = target.Service.GetIngredients().Select(item => item.Ingredient.Name).ToList();
        Assert.Equal(new[] { "Мука" }, names);
    }

    [Fact]
    public void Import_EmptySnapshot_ClearsBusinessData()
    {
        using var target = new TestDatabase();
        FilledSnapshot(target);

        new SnapshotImporter(target.Database).Import(EmptySnapshot());

        Assert.Empty(target.Service.GetIngredients());
        Assert.Empty(new PackagingService(target.Database).GetItems());
        Assert.Empty(new CustomerService(target.Database).GetCustomers());
    }

    [Fact]
    public void Import_KeepsLocalSettingsAndProfile()
    {
        using var source = new TestDatabase();
        var snapshot = FilledSnapshot(source);

        using var target = new TestDatabase();
        var settings = new SettingsService(target.Database);
        settings.Theme = SettingsService.ThemeDark;
        new ProfileService(target.Database).SaveProfile("Патиссерия", "Анна", "", "", "", null);

        new SnapshotImporter(target.Database).Import(snapshot);

        Assert.Equal(SettingsService.ThemeDark, new SettingsService(target.Database).Theme);
        Assert.Equal("Патиссерия", new ProfileService(target.Database).GetProfile().Name);
    }

    [Fact]
    public void Import_RoundTripsThroughExport()
    {
        using var source = new TestDatabase();
        var original = FilledSnapshot(source);

        using var target = new TestDatabase();
        new SnapshotImporter(target.Database).Import(original);

        var reExported = new SnapshotExporter(target.Database).Export(TakenAt);

        Assert.Equal(original.RowCounts, reExported.RowCounts);
        Assert.Equal(original.TotalRows, reExported.TotalRows);

        var originalOrder = Assert.Single(original.Orders);
        var reExportedOrder = Assert.Single(reExported.Orders);
        Assert.Equal(originalOrder.Id, reExportedOrder.Id);
        Assert.Equal(originalOrder.Number, reExportedOrder.Number);
        Assert.Equal(originalOrder.TotalPriceKopecks, reExportedOrder.TotalPriceKopecks);
        Assert.Equal(originalOrder.DueDate, reExportedOrder.DueDate);

        var originalItem = Assert.Single(original.OrderItems);
        var reExportedItem = Assert.Single(reExported.OrderItems);
        Assert.Equal(originalItem.CostEstimated, reExportedItem.CostEstimated);
        Assert.Equal(originalItem.QuantityMilli, reExportedItem.QuantityMilli);

        var originalRecipe = Assert.Single(original.Recipes);
        var reExportedRecipe = Assert.Single(reExported.Recipes);
        Assert.Equal(originalRecipe.PhotoBase64, reExportedRecipe.PhotoBase64);
        Assert.Equal(originalRecipe.OutputQuantityMilli, reExportedRecipe.OutputQuantityMilli);
    }

    [Fact]
    public void Import_AssignsProfileIdOne()
    {
        using var source = new TestDatabase();
        var snapshot = FilledSnapshot(source);

        using var target = new TestDatabase();
        new SnapshotImporter(target.Database).Import(snapshot);

        using var connection = target.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT profile_id FROM orders;";
        Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar()));
    }
}
