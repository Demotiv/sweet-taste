using SweetTaste.Core.Domain;
using SweetTaste.Core.Services;
using SweetTaste.Core.Storage;
using SweetTaste.Core.Sync;

namespace SweetTaste.Core.Tests;

public class SnapshotExporterTests
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    private static DateOnly Tomorrow => Today.AddDays(1);

    private static readonly DateTimeOffset TakenAt = new(2026, 11, 18, 12, 0, 0, TimeSpan.FromHours(3));

    [Fact]
    public void EmptyDatabase_ProducesEmptySnapshot()
    {
        using var db = new TestDatabase();
        var exporter = new SnapshotExporter(db.Database);

        var snapshot = exporter.Export(TakenAt);

        Assert.Equal(15, snapshot.SchemaVersion);
        Assert.Equal(TakenAt, snapshot.TakenAt);
        Assert.Equal(0, snapshot.TotalRows);
        Assert.Empty(snapshot.Ingredients);
        Assert.Empty(snapshot.Orders);
        Assert.Empty(snapshot.RecipeIngredients);
    }

    [Fact]
    public void Snapshot_ContainsEveryBusinessTable()
    {
        using var db = new TestDatabase();
        var flour = db.Service.AddIngredient("Мука", "Макфа", Units.Grams, "Сыпучие");
        db.Service.AddPurchase(flour.Id, Today, "Магнит", 1000m, 100m);

        var recipes = new RecipeService(db.Database, db.Service);
        var cake = recipes.CreateRecipe("Кекс", "Выпечка", 10m, RecipeUnits.Pieces, 100,
            new[] { (flour.Id, 500m) }, "Смешать и запечь.");

        var orders = new OrderService(db.Database, recipes);
        var order = orders.CreateOrder("Анна", "Смирнова", "+7 900 111-22-33", Tomorrow, null,
            new[] { (cake.Id, 2m) });

        var packaging = new PackagingService(db.Database);
        var box = packaging.CreateItem("Коробка", "20x20", 50m, "Картон");
        orders.SetOrderPackaging(order.Id, new[] { (box.Id, 1) });

        var snapshot = new SnapshotExporter(db.Database).Export(TakenAt);

        Assert.Single(snapshot.IngredientCategories);
        Assert.Single(snapshot.RecipeCategories);
        Assert.Single(snapshot.PackagingCategories);
        Assert.Single(snapshot.Ingredients);
        Assert.Single(snapshot.Purchases);
        Assert.Single(snapshot.Recipes);
        Assert.Single(snapshot.RecipeIngredients);
        Assert.Single(snapshot.Customers);
        Assert.Single(snapshot.Orders);
        Assert.Single(snapshot.OrderItems);
        Assert.Single(snapshot.PackagingItems);
        Assert.Single(snapshot.OrderPackaging);
        Assert.Equal(12, snapshot.TotalRows);
    }

    [Fact]
    public void Snapshot_KeepsScaledIntegersAndUnits()
    {
        using var db = new TestDatabase();
        var flour = db.Service.AddIngredient("Мука", null, Units.Grams);
        db.Service.AddPurchase(flour.Id, Today, null, 1500m, 123.45m);

        var recipes = new RecipeService(db.Database, db.Service);
        var cake = recipes.CreateRecipe("Кекс", null, 2.5m, RecipeUnits.Kilograms, 120,
            new[] { (flour.Id, 0.75m) });

        var snapshot = new SnapshotExporter(db.Database).Export(TakenAt);

        var purchase = Assert.Single(snapshot.Purchases);
        Assert.Equal(1_500_000L, purchase.QuantityMilli);
        Assert.Equal(12_345L, purchase.TotalPriceKopecks);
        Assert.Equal(Today, purchase.PurchasedOn);

        var recipe = Assert.Single(snapshot.Recipes);
        Assert.Equal(2_500L, recipe.OutputQuantityMilli);
        Assert.Equal(RecipeUnits.Kilograms, recipe.OutputUnit);
        Assert.Equal(120, recipe.MarkupPercent);

        var line = Assert.Single(snapshot.RecipeIngredients);
        Assert.Equal(750L, line.AmountMilli);
        Assert.Equal(0, line.Position);
    }

    [Fact]
    public void Snapshot_KeepsOrderMoneyAndStatus()
    {
        using var db = new TestDatabase();
        var flour = db.Service.AddIngredient("Мука", null, Units.Grams);
        db.Service.AddPurchase(flour.Id, Today, null, 1000m, 100m);

        var recipes = new RecipeService(db.Database, db.Service);
        var cake = recipes.CreateRecipe("Кекс", null, 10m, RecipeUnits.Pieces, 100,
            new[] { (flour.Id, 500m) });

        var orders = new OrderService(db.Database, recipes);
        var order = orders.CreateOrder("Анна", null, "+7 900 111-22-33", Tomorrow, "к 18:00",
            new[] { (cake.Id, 2m) }, paidAmount: 1m, discountAmount: 5m);
        orders.SetOrderStatus(order.Id, OrderStatus.Cooking);

        var snapshot = new SnapshotExporter(db.Database).Export(TakenAt);

        var saved = Assert.Single(snapshot.Orders);
        Assert.Equal(order.Number, saved.Number);
        Assert.Equal("Cooking", saved.Status);
        Assert.Equal("к 18:00", saved.Comment);
        Assert.Equal(Tomorrow, saved.DueDate);
        Assert.Equal(1_00L, saved.PaidAmountKopecks);
        Assert.Equal(5_00L, saved.DiscountKopecks);
        Assert.NotNull(saved.CustomerId);

        var item = Assert.Single(snapshot.OrderItems);
        Assert.Equal(saved.Id, item.OrderId);
        Assert.Equal(cake.Id, item.RecipeId);
        Assert.Equal(2_000L, item.QuantityMilli);
        Assert.False(item.CostEstimated);
    }

    [Fact]
    public void Snapshot_KeepsSoftDeletedRows()
    {
        using var db = new TestDatabase();
        var flour = db.Service.AddIngredient("Мука", null, Units.Grams);
        db.Service.MoveIngredientToTrash(flour.Id);

        var snapshot = new SnapshotExporter(db.Database).Export(TakenAt);

        var saved = Assert.Single(snapshot.Ingredients);
        Assert.NotNull(saved.DeletedAt);
    }

    [Fact]
    public void Snapshot_EncodesRecipePhotoAsBase64()
    {
        using var db = new TestDatabase();
        var flour = db.Service.AddIngredient("Мука", null, Units.Grams);
        var recipes = new RecipeService(db.Database, db.Service);
        var cake = recipes.CreateRecipe("Кекс", null, 10m, RecipeUnits.Pieces, 100,
            new[] { (flour.Id, 500m) });

        var photo = new byte[] { 1, 2, 3, 250, 251 };
        recipes.SetRecipePhoto(cake.Id, photo);

        var snapshot = new SnapshotExporter(db.Database).Export(TakenAt);

        var saved = Assert.Single(snapshot.Recipes);
        Assert.Equal(Convert.ToBase64String(photo), saved.PhotoBase64);
    }

    [Fact]
    public void Snapshot_LeavesPhotoNullWhenRecipeHasNone()
    {
        using var db = new TestDatabase();
        var flour = db.Service.AddIngredient("Мука", null, Units.Grams);
        var recipes = new RecipeService(db.Database, db.Service);
        recipes.CreateRecipe("Кекс", null, 10m, RecipeUnits.Pieces, 100, new[] { (flour.Id, 500m) });

        var snapshot = new SnapshotExporter(db.Database).Export(TakenAt);

        Assert.Null(Assert.Single(snapshot.Recipes).PhotoBase64);
    }

    [Fact]
    public void Snapshot_ExcludesDeviceSettingsAndProfile()
    {
        using var db = new TestDatabase();
        var settings = new SettingsService(db.Database);
        settings.Theme = SettingsService.ThemeDark;

        var profile = new ProfileService(db.Database);
        profile.SaveProfile("Кондитерская", "Анна", "@anna", "@anna", "example.ru", null);

        var snapshot = new SnapshotExporter(db.Database).Export(TakenAt);

        Assert.Equal(0, snapshot.TotalRows);
        Assert.DoesNotContain("app_settings", snapshot.RowCounts.Keys);
        Assert.DoesNotContain("confectioner_profile", snapshot.RowCounts.Keys);
    }

    [Fact]
    public void RowCounts_MatchCollectionSizes()
    {
        using var db = new TestDatabase();
        var flour = db.Service.AddIngredient("Мука", null, Units.Grams);
        var sugar = db.Service.AddIngredient("Сахар", null, Units.Grams);
        db.Service.AddPurchase(flour.Id, Today, null, 1000m, 100m);
        db.Service.AddPurchase(sugar.Id, Today, null, 1000m, 200m);

        var snapshot = new SnapshotExporter(db.Database).Export(TakenAt);

        Assert.Equal(2, snapshot.RowCounts[SyncTables.Ingredients]);
        Assert.Equal(2, snapshot.RowCounts[SyncTables.Purchases]);
        Assert.Equal(0, snapshot.RowCounts[SyncTables.Orders]);
        Assert.Equal(4, snapshot.TotalRows);
    }
}
