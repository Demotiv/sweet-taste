using SweetTaste.Core.Domain;
using SweetTaste.Core.Services;
using SweetTaste.Core.Storage;

namespace SweetTaste.Core.Tests;

public class ProfileTests
{
    [Fact]
    public void Profile_StartsEmpty()
    {
        using var db = new TestDatabase();
        var profile = new ProfileService(db.Database).GetProfile();

        Assert.Equal(string.Empty, profile.Name);
        Assert.Equal(string.Empty, profile.OwnerName);
        Assert.Null(profile.Photo);
    }

    [Fact]
    public void Profile_SavesAndReloads()
    {
        using var db = new TestDatabase();
        var service = new ProfileService(db.Database);
        var photo = new byte[] { 1, 2, 3, 4 };

        service.SaveProfile("Sweet Taste", "Анна Смирнова", "@sweet", "@sweet_taste",
            "https://example.com", photo);

        var reopened = new ProfileService(new AppDatabase(db.Database.FilePath)).GetProfile();

        Assert.Equal("Sweet Taste", reopened.Name);
        Assert.Equal("Анна Смирнова", reopened.OwnerName);
        Assert.Equal("@sweet", reopened.Instagram);
        Assert.Equal("@sweet_taste", reopened.Telegram);
        Assert.Equal("https://example.com", reopened.Website);
        Assert.Equal(photo, reopened.Photo);
    }

    [Fact]
    public void Photo_CanBeRemoved()
    {
        using var db = new TestDatabase();
        var service = new ProfileService(db.Database);

        service.SaveProfile("Торты", "Анна", "", "", "", new byte[] { 9, 9, 9 });
        var withoutPhoto = service.SaveProfile("Торты", "Анна", "", "", "", null);

        Assert.Null(withoutPhoto.Photo);
        Assert.Null(service.GetProfile().Photo);
    }

    [Fact]
    public void Export_CreatesFullConsistentCopy()
    {
        using var db = new TestDatabase();
        var flour = db.Service.AddIngredient("Мука", null, Units.Grams, "Сухие");
        db.Service.AddPurchase(flour.Id, DateOnly.FromDateTime(DateTime.Now), null, 1000m, 100m);

        var recipes = new RecipeService(db.Database, db.Service);
        var cake = recipes.CreateRecipe("Кекс", null, 10m, RecipeUnits.Pieces, 100,
            new[] { (flour.Id, 500m) });

        var orders = new OrderService(db.Database, recipes);
        orders.CreateOrder("Анна", "Смирнова", "+7 900 111-22-33",
            DateOnly.FromDateTime(DateTime.Now).AddDays(1), null, new[] { (cake.Id, 1m) });

        var profile = new ProfileService(db.Database);
        profile.SaveProfile("Торты", "Анна", "@anna", "", "", new byte[] { 7, 7, 7 });

        var settings = new SettingsService(db.Database);
        settings.Theme = SettingsService.ThemeDark;

        var directory = Path.Combine(db.Directory, "export");
        var path = db.Database.ExportTo(Path.Combine(directory, "copy.db"));

        Assert.True(File.Exists(path));
        Assert.Throws<IOException>(() => db.Database.ExportTo(path));

        var copy = new AppDatabase(path);
        var copyInventory = new InventoryService(copy);
        var copyRecipes = new RecipeService(copy, copyInventory);
        var copyOrders = new OrderService(copy, copyRecipes);
        var copyProfile = new ProfileService(copy).GetProfile();
        var copySettings = new SettingsService(copy);

        Assert.Single(copyInventory.GetIngredients());
        Assert.Single(copyRecipes.GetRecipes());
        var order = Assert.Single(copyOrders.GetOrders());
        Assert.NotNull(order.CustomerId);
        Assert.Equal("Торты", copyProfile.Name);
        Assert.Equal(new byte[] { 7, 7, 7 }, copyProfile.Photo);
        Assert.Equal(SettingsService.ThemeDark, copySettings.Theme);
    }

    [Fact]
    public void Profile_RejectsTooLongValuesAndBigPhoto()
    {
        using var db = new TestDatabase();
        var service = new ProfileService(db.Database);

        Assert.Throws<ValidationException>(() =>
            service.SaveProfile(new string('а', 81), "Анна", "", "", "", null));

        Assert.Throws<ValidationException>(() =>
            service.SaveProfile("Торты", "Анна", "", "", new string('x', 201), null));

        Assert.Throws<ValidationException>(() =>
            service.SaveProfile("Торты", "Анна", "", "", "", new byte[4_000_001]));
    }

    [Fact]
    public void Data_IsLinkedToProfile()
    {
        using var db = new TestDatabase();
        var flour = db.Service.AddIngredient("Мука", null, Units.Grams, "Сухие");
        db.Service.AddPurchase(flour.Id, DateOnly.FromDateTime(DateTime.Now), null, 1000m, 100m);

        var recipes = new RecipeService(db.Database, db.Service);
        var cake = recipes.CreateRecipe("Кекс", "Выпечка", 10m, RecipeUnits.Pieces, 100,
            new[] { (flour.Id, 500m) });

        var orders = new OrderService(db.Database, recipes);
        orders.CreateOrder("Иван", null, "1", DateOnly.FromDateTime(DateTime.Now).AddDays(1), null,
            new[] { (cake.Id, 1m) });

        var packaging = new PackagingService(db.Database);
        packaging.CreateItem("Коробка", "20×20", 100m, "Торты");

        using var connection = db.Database.Open();
        foreach (var table in new[]
                 {
                     "ingredient_categories", "ingredients", "purchases",
                     "recipe_categories", "recipes", "orders",
                     "packaging_categories", "packaging_items",
                 })
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT COUNT(*), COALESCE(SUM(CASE WHEN profile_id = 1 THEN 1 ELSE 0 END), 0)
                FROM {table};
                """;
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            var total = reader.GetInt64(0);
            var linked = reader.GetInt64(1);
            Assert.True(total > 0, $"{table}: нет строк для проверки");
            Assert.Equal(total, linked);
        }
    }
}
