using SweetTaste.Core.Domain;
using SweetTaste.Core.Services;

namespace SweetTaste.Core.Tests;

public class IngredientTests
{
    [Fact]
    public void NewDatabase_StartsEmpty()
    {
        using var db = new TestDatabase();

        Assert.Empty(db.Service.GetIngredients());

        var overview = db.Service.GetOverview(DateOnly.FromDateTime(DateTime.Now));
        Assert.Equal(0, overview.IngredientCount);
        Assert.Equal(0, overview.PurchaseCount);
        Assert.Equal(0m, overview.SpentThisMonth);
    }

    [Fact]
    public void AddIngredient_TrimsFieldsAndNormalizesUnit()
    {
        using var db = new TestDatabase();

        var ingredient = db.Service.AddIngredient("  Творожный сыр  ", "  Hochland  ", " г ");

        Assert.Equal("Творожный сыр", ingredient.Name);
        Assert.Equal("Hochland", ingredient.Brand);
        Assert.Equal(Units.Grams, ingredient.Unit);
        Assert.NotEqual(Guid.Empty, ingredient.Id);
        Assert.Single(db.Service.GetIngredients());
    }

    [Fact]
    public void AddIngredient_RejectsDuplicateIgnoringRussianCase()
    {
        using var db = new TestDatabase();
        db.Service.AddIngredient("Творожный сыр", null, Units.Grams);

        var error = Assert.Throws<ValidationException>(
            () => db.Service.AddIngredient("творожный СЫР", null, Units.Grams));

        Assert.Contains("уже есть", error.Message);
    }

    [Fact]
    public void AddIngredient_RejectsEmptyNameAndUnknownUnit()
    {
        using var db = new TestDatabase();

        Assert.Throws<ValidationException>(() => db.Service.AddIngredient("   ", null, Units.Grams));
        Assert.Throws<ValidationException>(() => db.Service.AddIngredient("Сыр", null, "кг"));
    }

    [Fact]
    public void UpdateIngredient_ChangesNameAndBrand()
    {
        using var db = new TestDatabase();
        var ingredient = db.Service.AddIngredient("Сыр", "Бренд", Units.Grams);

        var updated = db.Service.UpdateIngredient(ingredient.Id, "Творожный сыр", "Hochland", Units.Grams);

        Assert.Equal("Творожный сыр", updated.Name);
        Assert.Equal("Hochland", updated.Brand);
        Assert.Equal(ingredient.Id, updated.Id);
        Assert.Equal(
            "Творожный сыр",
            db.Service.GetIngredients().Single().Ingredient.Name);
    }

    [Fact]
    public void UpdateIngredient_RejectsDuplicateName()
    {
        using var db = new TestDatabase();
        db.Service.AddIngredient("Сыр", null, Units.Grams);
        var second = db.Service.AddIngredient("Масло", null, Units.Grams);

        Assert.Throws<ValidationException>(
            () => db.Service.UpdateIngredient(second.Id, "сыр", null, Units.Grams));
    }

    [Fact]
    public void UnitChange_IsBlockedWhenPurchasesExist()
    {
        using var db = new TestDatabase();
        var ingredient = db.Service.AddIngredient("Сыр", null, Units.Grams);
        db.Service.AddPurchase(ingredient.Id, DateOnly.FromDateTime(DateTime.Now), "Магнит", 200m, 100m);

        var error = Assert.Throws<ValidationException>(
            () => db.Service.UpdateIngredient(ingredient.Id, "Сыр", null, Units.Pieces));

        Assert.Contains("единицу измерения", error.Message);
    }

    [Fact]
    public void DeleteIngredient_IsBlockedWhilePurchasesExist()
    {
        using var db = new TestDatabase();
        var ingredient = db.Service.AddIngredient("Сыр", null, Units.Grams);
        var purchase = db.Service.AddPurchase(
            ingredient.Id, DateOnly.FromDateTime(DateTime.Now), null, 200m, 100m);

        Assert.Throws<ValidationException>(() => db.Service.DeleteIngredient(ingredient.Id));

        db.Service.DeletePurchase(purchase.Id);
        db.Service.DeleteIngredient(ingredient.Id);

        Assert.Empty(db.Service.GetIngredients());
    }

    [Fact]
    public void AddIngredient_WithCategory_StoresAndReusesCategoryIgnoringCase()
    {
        using var db = new TestDatabase();

        var cheese = db.Service.AddIngredient("Творожный сыр", "Hochland", Units.Grams, "Молочное");
        var milk = db.Service.AddIngredient("Молоко", null, Units.Milliliters, "молочное");

        Assert.NotNull(cheese.CategoryId);
        Assert.Equal(cheese.CategoryId, milk.CategoryId);
        Assert.Single(db.Service.GetCategoryNames());
        Assert.Equal("Молочное", db.Service.GetCategoryNames()[0]);

        var summary = db.Service.GetIngredients().Single(item => item.Ingredient.Id == cheese.Id);
        Assert.Equal("Молочное", summary.CategoryName);
    }

    [Fact]
    public void UpdateIngredient_CanChangeCategoryAndKeepsPurchases()
    {
        using var db = new TestDatabase();
        var ingredient = db.Service.AddIngredient("Сыр", null, Units.Grams, "Молочное");
        db.Service.AddPurchase(ingredient.Id, DateOnly.FromDateTime(DateTime.Now), "Магнит", 200m, 100m);

        var updated = db.Service.UpdateIngredient(ingredient.Id, "Сыр", null, Units.Grams, "Другое");

        Assert.NotEqual(updated.CategoryId, ingredient.CategoryId);
        var summary = db.Service.GetIngredients().Single();
        Assert.Equal("Другое", summary.CategoryName);
        Assert.Equal(1, summary.PurchaseCount);
    }

    [Fact]
    public void TrashIngredient_HidesAndRestoresWithPurchases()
    {
        using var db = new TestDatabase();
        var ingredient = db.Service.AddIngredient("Сыр", null, Units.Grams, "Молочное");
        db.Service.AddPurchase(ingredient.Id, DateOnly.FromDateTime(DateTime.Now), "Магнит", 200m, 100m);

        db.Service.MoveIngredientToTrash(ingredient.Id);

        Assert.Empty(db.Service.GetIngredients());
        var deleted = Assert.Single(db.Service.GetDeletedIngredients());
        Assert.Equal(ingredient.Id, deleted.Ingredient.Id);
        Assert.Equal("Молочное", deleted.CategoryName);
        Assert.Equal(1, deleted.PurchaseCount);

        db.Service.RestoreIngredient(ingredient.Id);

        Assert.Single(db.Service.GetIngredients());
        Assert.Empty(db.Service.GetDeletedIngredients());
    }

    [Fact]
    public void DeleteCategory_MovesIngredientsToNoCategory()
    {
        using var db = new TestDatabase();
        db.Service.AddIngredient("Сыр", null, Units.Grams, "Молочное");
        db.Service.AddIngredient("Молоко", null, Units.Milliliters, "Молочное");

        db.Service.DeleteCategory("молочное");

        Assert.Empty(db.Service.GetCategoryNames());
        Assert.All(db.Service.GetIngredients(), summary =>
        {
            Assert.Null(summary.CategoryName);
            Assert.Null(summary.Ingredient.CategoryId);
        });
    }

    [Fact]
    public void DeleteIngredient_WithoutPurchases_RemovesIt()
    {
        using var db = new TestDatabase();
        var ingredient = db.Service.AddIngredient("Сыр", null, Units.Grams);

        db.Service.DeleteIngredient(ingredient.Id);

        Assert.Empty(db.Service.GetIngredients());
    }

    [Fact]
    public void RenameCategory_KeepsIngredients()
    {
        using var db = new TestDatabase();
        db.Service.AddIngredient("Мука", null, Units.Grams, "Сухие");

        db.Service.RenameCategory("Сухие", "Сыпучие");

        Assert.Equal("Сыпучие", db.Service.GetIngredients().Single().CategoryName);
        Assert.Contains("Сыпучие", db.Service.GetCategoryNames());
    }

    [Fact]
    public void CategoryName_ReservedForNoCategory_IsRejected()
    {
        using var db = new TestDatabase();
        db.Service.AddIngredient("Мука", null, Units.Grams, "Сухие");

        Assert.Throws<ValidationException>(() =>
            db.Service.AddIngredient("Сахар", null, Units.Grams, "Без категории"));
        Assert.Throws<ValidationException>(() =>
            db.Service.RenameCategory("Сухие", "без категории"));
    }
}
