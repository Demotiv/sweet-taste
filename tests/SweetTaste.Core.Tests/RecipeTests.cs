using Microsoft.Data.Sqlite;
using SweetTaste.Core.Domain;
using SweetTaste.Core.Services;
using SweetTaste.Core.Storage;

namespace SweetTaste.Core.Tests;

public class RecipeTests
{
    private static RecipeService CreateRecipeService(TestDatabase db) =>
        new(db.Database, db.Service);

    private static (TestDatabase Db, RecipeService Recipes, Guid Flour, Guid Sugar, Guid Butter) CreateBase()
    {
        var db = new TestDatabase();
        var flour = db.Service.AddIngredient("Мука", null, Units.Grams);
        var sugar = db.Service.AddIngredient("Сахар", null, Units.Grams);
        var butter = db.Service.AddIngredient("Масло", null, Units.Grams);

        var today = DateOnly.FromDateTime(DateTime.Now);
        db.Service.AddPurchase(flour.Id, today, "Магнит", 1000m, 100m);   // 0.10 ₽/г
        db.Service.AddPurchase(sugar.Id, today, "Магнит", 1000m, 200m);   // 0.20 ₽/г
        db.Service.AddPurchase(butter.Id, today, "Магнит", 1000m, 1000m); // 1.00 ₽/г

        var recipes = CreateRecipeService(db);
        return (db, recipes, flour.Id, sugar.Id, butter.Id);
    }

    [Fact]
    public void CreateRecipe_CalculatesCostAndPrice()
    {
        var (db, recipes, flour, sugar, butter) = CreateBase();
        using var _ = db;

        var recipe = recipes.CreateRecipe(
            "Кекс",
            "Капкейки",
            10,
            RecipeUnits.Pieces,
            100,
            new[] { (flour, 500m), (sugar, 200m), (butter, 100m) });

        var summary = recipes.GetRecipes().Single();

        Assert.Equal(recipe.Id, summary.Recipe.Id);
        Assert.Equal("Капкейки", summary.CategoryName);
        Assert.Equal(3, summary.IngredientCount);
        Assert.Equal(190m, summary.BatchCost);
        Assert.Equal(19m, summary.CostPerUnit);
        Assert.Equal(38m, summary.PricePerUnit);
    }

    [Fact]
    public void Details_IncludeLineCostsAndPrices()
    {
        var (db, recipes, flour, sugar, butter) = CreateBase();
        using var _ = db;

        var recipe = recipes.CreateRecipe(
            "Кекс",
            null,
            10,
            RecipeUnits.Pieces,
            50,
            new[] { (flour, 500m), (sugar, 200m), (butter, 100m) });

        var details = recipes.GetRecipeDetails(recipe.Id);

        Assert.Equal(3, details.Lines.Count);
        Assert.Equal(50m, details.Lines.Single(line => line.Ingredient.Id == flour).Cost);
        Assert.Equal(0.10m, details.Lines.Single(line => line.Ingredient.Id == flour).AveragePricePerUnit);
        Assert.Equal(190m, details.BatchCost);
        Assert.Equal(19m, details.CostPerUnit);
        Assert.Equal(29m, details.PricePerUnit); // 19 * 1.5 = 28.5 → вверх до 29
    }

    [Fact]
    public void UpdateRecipe_RecalculatesAndKeepsCategory()
    {
        var (db, recipes, flour, sugar, butter) = CreateBase();
        using var _ = db;

        var recipe = recipes.CreateRecipe(
            "Кекс", "Капкейки", 10, RecipeUnits.Pieces, 100,
            new[] { (flour, 500m), (sugar, 200m), (butter, 100m) });

        var updated = recipes.UpdateRecipe(
            recipe.Id, "Большой кекс", "Торты", 5, RecipeUnits.Pieces, 200,
            new[] { (flour, 1000m), (sugar, 400m), (butter, 200m) });

        var summary = recipes.GetRecipes().Single();

        Assert.Equal("Большой кекс", updated.Name);
        Assert.Equal("Торты", summary.CategoryName);
        Assert.Equal(380m, summary.BatchCost);
        Assert.Equal(76m, summary.CostPerUnit);
        Assert.Equal(228m, summary.PricePerUnit);
    }

    [Fact]
    public void Categories_AreReusedIgnoringCase()
    {
        var (db, recipes, flour, sugar, butter) = CreateBase();
        using var _ = db;

        recipes.CreateRecipe("Кекс", "Капкейки", 10, RecipeUnits.Pieces, 100,
            new[] { (flour, 100m), (sugar, 10m), (butter, 10m) });
        recipes.CreateRecipe("Маффин", "капкейки", 8, RecipeUnits.Pieces, 100,
            new[] { (flour, 100m), (sugar, 10m), (butter, 10m) });

        var categories = recipes.GetCategoryNames();

        Assert.Single(categories);
        Assert.Equal("Капкейки", categories[0]);
    }

    [Fact]
    public void CreateRecipe_RejectsDuplicateLinesAndUnknownIngredient()
    {
        var (db, recipes, flour, sugar, butter) = CreateBase();
        using var _ = db;

        Assert.Throws<ValidationException>(() => recipes.CreateRecipe(
            "Дубль", null, 10, RecipeUnits.Pieces, 100,
            new[] { (flour, 100m), (flour, 50m) }));

        Assert.Throws<ValidationException>(() => recipes.CreateRecipe(
            "Неизвестный", null, 10, RecipeUnits.Pieces, 100,
            new[] { (Guid.NewGuid(), 100m) }));
    }

    [Fact]
    public void CreateRecipe_RejectsEmptyInput()
    {
        var (db, recipes, flour, sugar, butter) = CreateBase();
        using var _ = db;

        Assert.Throws<ValidationException>(() => recipes.CreateRecipe(
            "Пусто", null, 10, RecipeUnits.Pieces, 100, Array.Empty<(Guid, decimal)>()));

        Assert.Throws<ValidationException>(() => recipes.CreateRecipe(
            "  ", null, 10, RecipeUnits.Pieces, 100, new[] { (flour, 100m) }));

        Assert.Throws<ValidationException>(() => recipes.CreateRecipe(
            "Ноль", null, 0, RecipeUnits.Pieces, 100, new[] { (flour, 100m) }));

        Assert.Throws<ValidationException>(() => recipes.CreateRecipe(
            "Плохая единица", null, 10, "л", 100, new[] { (flour, 100m) }));

        Assert.Throws<ValidationException>(() => recipes.CreateRecipe(
            "Наценка", null, 10, RecipeUnits.Pieces, -1, new[] { (flour, 100m) }));
    }

    [Fact]
    public void DeleteRecipe_RemovesItAndFreesIngredients()
    {
        var (db, recipes, flour, sugar, butter) = CreateBase();
        using var _ = db;

        var recipe = recipes.CreateRecipe("Кекс", null, 10, RecipeUnits.Pieces, 100,
            new[] { (flour, 100m) });

        recipes.DeleteRecipe(recipe.Id);

        Assert.Empty(recipes.GetRecipes());
    }

    [Fact]
    public void IngredientUsedInRecipe_CannotBeDeleted()
    {
        var (db, recipes, flour, sugar, butter) = CreateBase();
        using var _ = db;

        var salt = db.Service.AddIngredient("Соль", null, Units.Grams);
        recipes.CreateRecipe("Кекс", null, 10, RecipeUnits.Pieces, 100,
            new[] { (salt.Id, 10m) });

        var error = Assert.Throws<ValidationException>(() => db.Service.DeleteIngredient(salt.Id));

        Assert.Contains("рецептах", error.Message);
        Assert.Contains("Кекс", error.Message);
    }

    [Fact]
    public void IngredientUsedInRecipe_UnitCannotBeChanged()
    {
        var (db, recipes, flour, sugar, butter) = CreateBase();
        using var _ = db;

        var salt = db.Service.AddIngredient("Соль", null, Units.Grams);
        recipes.CreateRecipe("Кекс", null, 10, RecipeUnits.Pieces, 100,
            new[] { (salt.Id, 10m) });

        var error = Assert.Throws<ValidationException>(
            () => db.Service.UpdateIngredient(salt.Id, "Соль", null, Units.Pieces));

        Assert.Contains("единицу измерения", error.Message);
    }

    [Fact]
    public void DeleteCategory_MovesRecipesToNoCategory()
    {
        var (db, recipes, flour, sugar, butter) = CreateBase();
        using var _ = db;

        recipes.CreateRecipe("Кекс", "Капкейки", 10, RecipeUnits.Pieces, 100,
            new[] { (flour, 100m), (sugar, 10m), (butter, 10m) });

        recipes.DeleteCategory("капкейки");

        Assert.Empty(recipes.GetCategoryNames());
        var summary = recipes.GetRecipes().Single();
        Assert.Null(summary.CategoryName);
        Assert.Null(summary.Recipe.CategoryId);
    }

    [Fact]
    public void TrashRecipe_HidesAndRestores()
    {
        var (db, recipes, flour, sugar, butter) = CreateBase();
        using var _ = db;

        var recipe = recipes.CreateRecipe("Кекс", "Капкейки", 10, RecipeUnits.Pieces, 100,
            new[] { (flour, 100m), (sugar, 10m), (butter, 10m) });

        recipes.MoveRecipeToTrash(recipe.Id);

        Assert.Empty(recipes.GetRecipes());
        var deleted = Assert.Single(recipes.GetDeletedRecipes());
        Assert.Equal(recipe.Id, deleted.Recipe.Id);
        Assert.Equal("Капкейки", deleted.CategoryName);

        recipes.RestoreRecipe(recipe.Id);

        Assert.Single(recipes.GetRecipes());
        Assert.Empty(recipes.GetDeletedRecipes());
    }

    [Fact]
    public void TrashedIngredient_StillCountsInRecipeCost()
    {
        var (db, recipes, flour, sugar, butter) = CreateBase();
        using var _ = db;

        recipes.CreateRecipe("Кекс", null, 10, RecipeUnits.Pieces, 100,
            new[] { (flour, 500m), (sugar, 200m), (butter, 100m) });

        db.Service.MoveIngredientToTrash(flour);

        var summary = recipes.GetRecipes().Single();
        Assert.Equal(190m, summary.BatchCost);

        var details = recipes.GetRecipeDetails(summary.Recipe.Id);
        Assert.Equal(3, details.Lines.Count);
    }

    [Fact]
    public void ForceDelete_RemovesPurchasesAndRecipeLines()
    {
        var (db, recipes, flour, sugar, butter) = CreateBase();
        using var _ = db;

        recipes.CreateRecipe("Кекс", null, 10, RecipeUnits.Pieces, 100,
            new[] { (flour, 100m) });

        db.Service.DeleteIngredient(flour, force: true);

        Assert.DoesNotContain(db.Service.GetIngredients(), item => item.Ingredient.Id == flour);
        var recipe = recipes.GetRecipes().Single();
        Assert.Equal(0, recipe.IngredientCount);
        Assert.Equal(0m, recipe.BatchCost);
    }

    [Fact]
    public void RecipeWithoutPurchasedIngredients_HasZeroCost()
    {
        var db = new TestDatabase();
        using var _ = db;
        var salt = db.Service.AddIngredient("Соль", null, Units.Grams);
        var recipes = CreateRecipeService(db);

        var recipe = recipes.CreateRecipe("Солёный кекс", null, 4, RecipeUnits.Pieces, 100,
            new[] { (salt.Id, 10m) });

        var summary = recipes.GetRecipes().Single();
        Assert.Equal(0m, summary.BatchCost);
        Assert.Equal(0m, summary.PricePerUnit);
        Assert.Equal(recipe.Id, summary.Recipe.Id);
    }

    [Fact]
    public void DataSurvivesReopen()
    {
        var (db, recipes, flour, sugar, butter) = CreateBase();
        using var _ = db;

        recipes.CreateRecipe("Кекс", "Капкейки", 10, RecipeUnits.Pieces, 100,
            new[] { (flour, 500m), (sugar, 200m), (butter, 100m) });

        var inventory = db.Reopen();
        var reopened = new RecipeService(new AppDatabase(db.Database.FilePath), inventory);
        var summary = reopened.GetRecipes().Single();

        Assert.Equal("Кекс", summary.Recipe.Name);
        Assert.Equal("Капкейки", summary.CategoryName);
        Assert.Equal(190m, summary.BatchCost);
        Assert.Equal(3, summary.Recipe.Ingredients.Count);
    }

    [Fact]
    public void MigrationFromV1_PreservesDataAndAddsRecipeTables()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sweet-taste-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "v1.db");

        try
        {
            using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE ingredients (
                        id TEXT NOT NULL PRIMARY KEY,
                        name TEXT NOT NULL,
                        brand TEXT NULL,
                        unit TEXT NOT NULL,
                        created_at TEXT NOT NULL,
                        updated_at TEXT NOT NULL
                    );
                    CREATE TABLE purchases (
                        id TEXT NOT NULL PRIMARY KEY,
                        ingredient_id TEXT NOT NULL,
                        purchased_on TEXT NOT NULL,
                        store TEXT NULL,
                        quantity_milli INTEGER NOT NULL,
                        total_price_kopecks INTEGER NOT NULL,
                        order_id TEXT NULL,
                        note TEXT NULL,
                        created_at TEXT NOT NULL
                    );
                    PRAGMA user_version = 1;
                    """;
                command.ExecuteNonQuery();

                using var insert = connection.CreateCommand();
                insert.CommandText = """
                    INSERT INTO ingredients (id, name, brand, unit, created_at, updated_at)
                    VALUES ($id, 'Мука', NULL, 'г', '2026-11-01T10:00:00+03:00', '2026-11-01T10:00:00+03:00');
                    """;
                insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
                insert.ExecuteNonQuery();
            }

            var database = new AppDatabase(path);
            var inventory = new InventoryService(database);
            var recipes = new RecipeService(database, inventory);

            Assert.Single(inventory.GetIngredients());
            Assert.Empty(recipes.GetRecipes());
            Assert.Empty(recipes.GetCategoryNames());
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void RecipeCategory_CanBeRenamed()
    {
        var (db, recipes, flour, sugar, butter) = CreateBase();
        using var _ = db;

        recipes.CreateRecipe("Кекс", "Выпечка", 10m, RecipeUnits.Pieces, 100,
            new[] { (flour, 500m) });

        recipes.RenameCategory("Выпечка", "Торты");

        var summary = recipes.GetRecipes().Single();
        Assert.Equal("Торты", summary.CategoryName);
        Assert.Contains("Торты", recipes.GetCategoryNames());
    }

    [Fact]
    public void RecipeCategory_ReservedForNoCategory_IsRejected()
    {
        var (db, recipes, flour, _, _) = CreateBase();
        using var _db = db;

        recipes.CreateRecipe("Кекс", "Выпечка", 10m, RecipeUnits.Pieces, 100,
            new[] { (flour, 500m) });

        Assert.Throws<ValidationException>(() =>
            recipes.CreateRecipe("Торт", "Без категории", 1m, RecipeUnits.Pieces, 100,
                new[] { (flour, 500m) }));
        Assert.Throws<ValidationException>(() =>
            recipes.RenameCategory("Выпечка", "Без категории"));

        Assert.Equal("Выпечка", recipes.GetRecipes().Single().CategoryName);
    }

    [Fact]
    public void Recipe_StoresTechnologyAndPhoto()
    {
        var (db, recipes, flour, sugar, butter) = CreateBase();
        using var _ = db;

        var recipe = recipes.CreateRecipe("Кекс", null, 10m, RecipeUnits.Pieces, 100,
            new[] { (flour, 500m) }, technology: "Смешать и выпекать 40 минут.");
        var photo = new byte[] { 1, 2, 3, 4 };

        recipes.SetRecipePhoto(recipe.Id, photo);

        var loaded = recipes.GetRecipes().Single(item => item.Recipe.Id == recipe.Id);
        Assert.Equal("Смешать и выпекать 40 минут.", loaded.Recipe.Technology);
        Assert.Equal(photo, loaded.Recipe.Photo);

        recipes.UpdateRecipe(recipe.Id, "Кекс", null, 10m, RecipeUnits.Pieces, 100,
            new[] { (flour, 500m) }, technology: "Новая технология.");

        var updated = recipes.GetRecipeDetails(recipe.Id).Recipe;
        Assert.Equal("Новая технология.", updated.Technology);
        Assert.Equal(photo, updated.Photo);

        recipes.SetRecipePhoto(recipe.Id, null);
        Assert.Null(recipes.GetRecipeDetails(recipe.Id).Recipe.Photo);
    }
}
