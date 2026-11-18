using SweetTaste.Core.Domain;
using SweetTaste.Core.Services;

namespace SweetTaste.Core.Tests;

public class PurchaseTests
{
    [Fact]
    public void AveragePrice_IsWeightedAcrossPurchases()
    {
        using var db = new TestDatabase();
        var cheese = db.Service.AddIngredient("Творожный сыр", "Hochland", Units.Grams);

        db.Service.AddPurchase(cheese.Id, new DateOnly(2026, 11, 2), "Пятёрочка", 200m, 100m);
        db.Service.AddPurchase(cheese.Id, new DateOnly(2026, 11, 16), "Магнит", 300m, 180m);

        var summary = db.Service.GetIngredients().Single();

        Assert.Equal(2, summary.PurchaseCount);
        Assert.Equal(500m, summary.TotalQuantity);
        Assert.Equal(280m, summary.TotalSpent);
        Assert.Equal(0.56m, summary.AveragePricePerUnit);
        Assert.Equal("Магнит", summary.LastPurchase?.Store);
        Assert.Equal(0.60m, summary.LastPurchase?.PricePerUnit);
    }

    [Fact]
    public void Details_ReturnHistoryNewestFirst()
    {
        using var db = new TestDatabase();
        var cheese = db.Service.AddIngredient("Сыр", null, Units.Grams);

        db.Service.AddPurchase(cheese.Id, new DateOnly(2026, 11, 2), "Пятёрочка", 200m, 100m);
        db.Service.AddPurchase(cheese.Id, new DateOnly(2026, 11, 16), "Магнит", 200m, 120m);

        var details = db.Service.GetDetails(cheese.Id);

        Assert.Equal(2, details.Purchases.Count);
        Assert.Equal("Магнит", details.Purchases[0].Store);
        Assert.Equal("Пятёрочка", details.Purchases[1].Store);
        Assert.Equal(220m, details.TotalSpent);
        Assert.Equal(400m, details.TotalQuantity);
        Assert.Equal(0.55m, details.AveragePricePerUnit);
    }

    [Fact]
    public void AddPurchase_RejectsFutureDate()
    {
        using var db = new TestDatabase();
        var cheese = db.Service.AddIngredient("Сыр", null, Units.Grams);
        var tomorrow = DateOnly.FromDateTime(DateTime.Now).AddDays(1);

        var error = Assert.Throws<ValidationException>(
            () => db.Service.AddPurchase(cheese.Id, tomorrow, null, 200m, 100m));

        Assert.Contains("будущем", error.Message);
    }

    [Fact]
    public void AddPurchase_RejectsZeroQuantityAndNegativePrice()
    {
        using var db = new TestDatabase();
        var cheese = db.Service.AddIngredient("Сыр", null, Units.Grams);
        var today = DateOnly.FromDateTime(DateTime.Now);

        Assert.Throws<ValidationException>(() => db.Service.AddPurchase(cheese.Id, today, null, 0m, 100m));
        Assert.Throws<ValidationException>(() => db.Service.AddPurchase(cheese.Id, today, null, 200m, -1m));
    }

    [Fact]
    public void AddPurchase_RequiresExistingIngredient()
    {
        using var db = new TestDatabase();

        Assert.Throws<ValidationException>(
            () => db.Service.AddPurchase(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.Now), null, 100m, 50m));
    }

    [Fact]
    public void ZeroPricePurchase_IsAllowedAndDoesNotBreakAverage()
    {
        using var db = new TestDatabase();
        var cheese = db.Service.AddIngredient("Сыр", null, Units.Grams);
        var today = DateOnly.FromDateTime(DateTime.Now);

        db.Service.AddPurchase(cheese.Id, today, "Акция", 200m, 0m);
        db.Service.AddPurchase(cheese.Id, today, "Магнит", 200m, 100m);

        var summary = db.Service.GetIngredients().Single();
        Assert.Equal(0.25m, summary.AveragePricePerUnit);
    }

    [Fact]
    public void DataSurvivesReopen()
    {
        using var db = new TestDatabase();
        var cheese = db.Service.AddIngredient("Сыр", "Hochland", Units.Grams);
        db.Service.AddPurchase(cheese.Id, new DateOnly(2026, 11, 10), "Лента", 200m, 120m);

        var reopened = db.Reopen();
        var summary = reopened.GetIngredients().Single();

        Assert.Equal("Сыр", summary.Ingredient.Name);
        Assert.Equal("Hochland", summary.Ingredient.Brand);
        Assert.Equal(1, summary.PurchaseCount);
        Assert.Equal(0.60m, summary.AveragePricePerUnit);
    }

    [Fact]
    public void Overview_CountsOnlyCurrentMonthPurchases()
    {
        using var db = new TestDatabase();
        var today = DateOnly.FromDateTime(DateTime.Now);
        var cheese = db.Service.AddIngredient("Сыр", null, Units.Grams);

        db.Service.AddPurchase(cheese.Id, today.AddMonths(-1), "Старый", 100m, 50m);
        db.Service.AddPurchase(cheese.Id, today, "Магнит", 200m, 120m);
        db.Service.AddPurchase(cheese.Id, today.AddDays(-1), "Лента", 300m, 150m);

        var overview = db.Service.GetOverview(today);

        Assert.Equal(1, overview.IngredientCount);
        Assert.Equal(3, overview.PurchaseCount);
        Assert.Equal(2, overview.PurchasesThisMonth);
        Assert.Equal(270m, overview.SpentThisMonth);
        Assert.Equal(150m, overview.LargestPurchaseThisMonth?.TotalPrice);
        Assert.Equal("Сыр", overview.LargestPurchaseIngredientName);
    }

    [Fact]
    public void UpdatePurchase_ChangesValuesAndRecalculatesAverage()
    {
        using var db = new TestDatabase();
        var cheese = db.Service.AddIngredient("Творожный сыр", null, Units.Grams);
        db.Service.AddPurchase(cheese.Id, new DateOnly(2026, 11, 2), "Пятёрочка", 200m, 100m);
        var editable = db.Service.AddPurchase(cheese.Id, new DateOnly(2026, 11, 10), "Магнит", 300m, 180m);

        var updated = db.Service.UpdatePurchase(
            editable.Id, new DateOnly(2026, 11, 12), "Лента", 250m, 200m, "Для чизкейка");

        Assert.Equal(new DateOnly(2026, 11, 12), updated.PurchasedOn);
        Assert.Equal("Лента", updated.Store);
        Assert.Equal(250m, updated.Quantity);
        Assert.Equal(200m, updated.TotalPrice);
        Assert.Equal("Для чизкейка", updated.Note);
        Assert.Equal(cheese.Id, updated.IngredientId);
        Assert.Equal(0.8m, updated.PricePerUnit);

        var summary = db.Service.GetIngredients().Single();
        Assert.Equal(300m, summary.TotalSpent);
        Assert.Equal(450m, summary.TotalQuantity);
        Assert.Equal(300m / 450m, summary.AveragePricePerUnit);
        Assert.Equal("Лента", summary.LastPurchase?.Store);

        var details = db.Service.GetDetails(cheese.Id);
        Assert.Equal("Для чизкейка", details.Purchases[0].Note);
    }

    [Fact]
    public void UpdatePurchase_ValidatesInputAndMissingId()
    {
        using var db = new TestDatabase();
        var cheese = db.Service.AddIngredient("Сыр", null, Units.Grams);
        var today = DateOnly.FromDateTime(DateTime.Now);
        var purchase = db.Service.AddPurchase(cheese.Id, today, "Магнит", 200m, 100m);

        Assert.Throws<ValidationException>(
            () => db.Service.UpdatePurchase(purchase.Id, today.AddDays(1), "Магнит", 200m, 100m));

        Assert.Throws<ValidationException>(
            () => db.Service.UpdatePurchase(purchase.Id, today, "Магнит", 0m, 100m));

        Assert.Throws<ValidationException>(
            () => db.Service.UpdatePurchase(purchase.Id, today, "Магнит", 200m, -5m));

        Assert.Throws<ValidationException>(
            () => db.Service.UpdatePurchase(Guid.NewGuid(), today, "Магнит", 200m, 100m));
    }

    [Fact]
    public void DailyBackup_CreatesFileOnce()
    {
        using var db = new TestDatabase();
        db.Service.AddIngredient("Сыр", null, Units.Grams);
        var today = DateOnly.FromDateTime(DateTime.Now);

        var first = db.Database.TryCreateDailyBackup(today);
        var second = db.Database.TryCreateDailyBackup(today);

        Assert.NotNull(first);
        Assert.True(File.Exists(first));
        Assert.Null(second);
    }
}
