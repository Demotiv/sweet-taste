using System.Globalization;
using Microsoft.Data.Sqlite;
using SweetTaste.Core.Domain;
using SweetTaste.Core.Storage;

namespace SweetTaste.Core.Services;

/// <summary>
/// Книга рецептов: состав, выход, наценка и расчёт цены.
/// Себестоимость считается по средним ценам ингредиентов из журнала закупок.
/// </summary>
public sealed class RecipeService
{
    private const int MaxNameLength = 80;
    private const int MaxCategoryLength = 60;
    private const decimal MaxOutputQuantity = 100_000m;
    private const int MaxMarkupPercent = 1_000;
    private const int MaxTechnologyLength = 5_000;
    private const int MaxPhotoBytes = 4_000_000;
    private const decimal MaxAmount = 1_000_000m;

    private readonly AppDatabase _database;
    private readonly InventoryService _inventory;

    public RecipeService(AppDatabase database, InventoryService inventory)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _database.Initialize();
    }

    public IReadOnlyList<RecipeSummary> GetRecipes()
    {
        using var connection = _database.Open();
        var prices = GetAveragePrices();
        var categories = LoadCategories(connection).ToDictionary(category => category.Id);
        var linesByRecipe = LoadAllLines(connection);

        var result = new List<RecipeSummary>();

        foreach (var recipe in LoadRecipes(connection).Where(recipe => recipe.DeletedAt is null))
        {
            var lines = linesByRecipe.TryGetValue(recipe.Id, out var stored) ? stored : new List<RecipeLine>();
            var recipeWithLines = recipe with { Ingredients = lines };
            var cost = CalculateCost(lines, prices);
            var costPerUnit = CalculateCostPerUnit(cost, recipe.OutputQuantity);
            var pricePerUnit = CalculatePricePerUnit(costPerUnit, recipe.MarkupPercent, recipe.OutputUnit);

            result.Add(new RecipeSummary(
                recipeWithLines,
                recipe.CategoryId is { } categoryId && categories.TryGetValue(categoryId, out var category)
                    ? category.Name
                    : null,
                cost,
                costPerUnit,
                pricePerUnit,
                lines.Count));
        }

        return result
            .OrderBy(summary => summary.Recipe.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public RecipeDetails GetRecipeDetails(Guid recipeId)
    {
        using var connection = _database.Open();
        var recipe = FindRecipe(connection, recipeId)
            ?? throw new ValidationException("Рецепт не найден.");

        var categoryName = recipe.CategoryId is { } categoryId
            ? LoadCategories(connection).FirstOrDefault(category => category.Id == categoryId)?.Name
            : null;

        var prices = GetAveragePrices();
        var ingredients = _inventory.GetIngredients(includeDeleted: true)
            .ToDictionary(summary => summary.Ingredient.Id);

        var lineDetails = new List<RecipeLineDetails>();
        decimal batchCost = 0m;

        foreach (var line in recipe.Ingredients.OrderBy(line => line.Position))
        {
            if (!ingredients.TryGetValue(line.IngredientId, out var summary))
            {
                continue;
            }

            var averagePrice = summary.AveragePricePerUnit;
            var cost = line.Amount * (averagePrice ?? 0m);
            batchCost += cost;

            lineDetails.Add(new RecipeLineDetails(
                summary.Ingredient,
                line.Amount,
                averagePrice,
                cost));
        }

        var costPerUnit = CalculateCostPerUnit(batchCost, recipe.OutputQuantity);

        return new RecipeDetails(
            recipe,
            categoryName,
            lineDetails,
            batchCost,
            costPerUnit,
            CalculatePricePerUnit(costPerUnit, recipe.MarkupPercent, recipe.OutputUnit));
    }

    public IReadOnlyList<string> GetCategoryNames()
    {
        using var connection = _database.Open();
        return LoadCategories(connection)
            .Select(category => category.Name)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public void DeleteCategory(string name)
    {
        var cleanName = (name ?? string.Empty).Trim();
        if (cleanName.Length == 0)
        {
            throw new ValidationException("Категория не найдена.");
        }

        using var connection = _database.Open();
        var category = LoadCategories(connection).FirstOrDefault(item =>
            string.Equals(item.Name, cleanName, StringComparison.CurrentCultureIgnoreCase))
            ?? throw new ValidationException("Категория не найдена.");

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM recipe_categories WHERE id = $id;";
        command.Parameters.AddWithValue("$id", category.Id.ToString());
        command.ExecuteNonQuery();
    }

    public void RenameCategory(string oldName, string newName)
    {
        var oldClean = (oldName ?? string.Empty).Trim();
        var newClean = (newName ?? string.Empty).Trim();

        if (newClean.Length == 0)
        {
            throw new ValidationException("Укажите новое название категории.");
        }

        if (newClean.Length > MaxCategoryLength)
        {
            throw new ValidationException($"Название категории длиннее {MaxCategoryLength} символов — сократите.");
        }

        EnsureCategoryNameAllowed(newClean);

        if (string.Equals(oldClean, newClean, StringComparison.Ordinal))
        {
            return;
        }

        using var connection = _database.Open();
        var categories = LoadCategories(connection);
        var category = categories.FirstOrDefault(item =>
            string.Equals(item.Name, oldClean, StringComparison.CurrentCultureIgnoreCase))
            ?? throw new ValidationException("Категория не найдена.");

        if (categories.Any(item => item.Id != category.Id &&
                                   string.Equals(item.Name, newClean, StringComparison.CurrentCultureIgnoreCase)))
        {
            throw new ValidationException($"Категория «{newClean}» уже есть.");
        }

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE recipe_categories SET name = $name WHERE id = $id;";
        command.Parameters.AddWithValue("$id", category.Id.ToString());
        command.Parameters.AddWithValue("$name", newClean);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<IngredientPrice> GetIngredientPrices() =>
        _inventory.GetIngredients()
            .Select(summary => new IngredientPrice(
                summary.Ingredient.Id,
                summary.Ingredient.Name,
                summary.Ingredient.Unit,
                summary.CategoryName,
                summary.AveragePricePerUnit))
            .OrderBy(price => price.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    public decimal CalculateCost(IEnumerable<(Guid IngredientId, decimal Amount)> lines)
    {
        var prices = GetAveragePrices();
        return lines.Sum(line => line.Amount * (prices.GetValueOrDefault(line.IngredientId) ?? 0m));
    }

    public decimal CalculateCostPerUnit(decimal batchCost, decimal outputQuantity) =>
        outputQuantity > 0m ? batchCost / outputQuantity : 0m;

    public decimal CalculatePricePerUnit(decimal costPerUnit, int markupPercent, string outputUnit) =>
        RecipeUnits.RoundPricePerUnit(costPerUnit * (1m + markupPercent / 100m), outputUnit);

    public Recipe CreateRecipe(
        string name,
        string? categoryName,
        decimal outputQuantity,
        string outputUnit,
        int markupPercent,
        IEnumerable<(Guid IngredientId, decimal Amount)> lines,
        string? technology = null)
    {
        var validated = ValidateInput(name, categoryName, outputQuantity, outputUnit, markupPercent, lines);
        var cleanTechnology = CleanTechnology(technology);

        using var connection = _database.Open();
        EnsureUniqueName(connection, validated.Name, exceptId: null);
        EnsureIngredientsExist(connection, validated.Lines);

        using var transaction = connection.BeginTransaction();
        var categoryId = ResolveCategory(connection, transaction, validated.CategoryName);

        var now = DateTimeOffset.Now;
        var recipe = new Recipe(
            Guid.NewGuid(),
            validated.Name,
            categoryId,
            validated.OutputQuantity,
            validated.OutputUnit,
            validated.MarkupPercent,
            CreateLines(validated.Lines),
            now,
            now,
            DeletedAt: null,
            cleanTechnology,
            Photo: null);

        InsertRecipe(connection, transaction, recipe);
        InsertLines(connection, transaction, recipe);

        transaction.Commit();
        return recipe;
    }

    public Recipe UpdateRecipe(
        Guid recipeId,
        string name,
        string? categoryName,
        decimal outputQuantity,
        string outputUnit,
        int markupPercent,
        IEnumerable<(Guid IngredientId, decimal Amount)> lines,
        string? technology = null)
    {
        var validated = ValidateInput(name, categoryName, outputQuantity, outputUnit, markupPercent, lines);
        var cleanTechnology = CleanTechnology(technology);

        using var connection = _database.Open();
        var existing = FindRecipe(connection, recipeId)
            ?? throw new ValidationException("Рецепт не найден.");

        EnsureUniqueName(connection, validated.Name, exceptId: recipeId);
        EnsureIngredientsExist(connection, validated.Lines);

        using var transaction = connection.BeginTransaction();
        var categoryId = ResolveCategory(connection, transaction, validated.CategoryName);

        var recipe = existing with
        {
            Name = validated.Name,
            CategoryId = categoryId,
            OutputQuantity = validated.OutputQuantity,
            OutputUnit = validated.OutputUnit,
            MarkupPercent = validated.MarkupPercent,
            Ingredients = CreateLines(validated.Lines),
            Technology = cleanTechnology,
            UpdatedAt = DateTimeOffset.Now,
        };

        UpdateRecipeRow(connection, transaction, recipe);

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM recipe_ingredients WHERE recipe_id = $id;";
            delete.Parameters.AddWithValue("$id", recipe.Id.ToString());
            delete.ExecuteNonQuery();
        }

        InsertLines(connection, transaction, recipe);
        transaction.Commit();

        return recipe;
    }

    public void SetRecipeMarkup(Guid recipeId, int markupPercent)
    {
        if (markupPercent < 0 || markupPercent > MaxMarkupPercent)
        {
            throw new ValidationException($"Наценка должна быть от 0 до {MaxMarkupPercent}%.");
        }

        using var connection = _database.Open();
        _ = FindRecipe(connection, recipeId) ?? throw new ValidationException("Рецепт не найден.");

        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE recipes
            SET markup_percent = $markup, updated_at = $updated
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", recipeId.ToString());
        command.Parameters.AddWithValue("$markup", markupPercent);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    /// <summary>Ставит или удаляет фото рецепта. null удаляет картинку.</summary>
    public void SetRecipePhoto(Guid recipeId, byte[]? photo)
    {
        if (photo is { Length: > MaxPhotoBytes })
        {
            throw new ValidationException("Фото слишком большое — выберите файл поменьше.");
        }

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE recipes
            SET photo = $photo, updated_at = $updated
            WHERE id = $id AND deleted_at IS NULL;
            """;
        command.Parameters.AddWithValue("$id", recipeId.ToString());
        command.Parameters.AddWithValue("$photo", (object?)photo ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));

        if (command.ExecuteNonQuery() == 0)
        {
            throw new ValidationException("Рецепт не найден.");
        }
    }

    public void DeleteRecipe(Guid recipeId)
    {        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM recipes WHERE id = $id;";
        command.Parameters.AddWithValue("$id", recipeId.ToString());

        if (command.ExecuteNonQuery() == 0)
        {
            throw new ValidationException("Рецепт не найден.");
        }
    }

    public void MoveRecipeToTrash(Guid recipeId)
    {
        using var connection = _database.Open();
        var now = DateTimeOffset.Now;

        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE recipes
            SET deleted_at = $deleted, updated_at = $updated
            WHERE id = $id AND deleted_at IS NULL;
            """;
        command.Parameters.AddWithValue("$id", recipeId.ToString());
        command.Parameters.AddWithValue("$deleted", now.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updated", now.ToString("O", CultureInfo.InvariantCulture));

        if (command.ExecuteNonQuery() == 0)
        {
            throw new ValidationException("Рецепт не найден.");
        }
    }

    public void RestoreRecipe(Guid recipeId)
    {
        using var connection = _database.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE recipes
            SET deleted_at = NULL, updated_at = $updated
            WHERE id = $id AND deleted_at IS NOT NULL;
            """;
        command.Parameters.AddWithValue("$id", recipeId.ToString());
        command.Parameters.AddWithValue("$updated", DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));

        if (command.ExecuteNonQuery() == 0)
        {
            throw new ValidationException("Рецепт в корзине не найден.");
        }
    }

    public IReadOnlyList<DeletedRecipe> GetDeletedRecipes()
    {
        using var connection = _database.Open();
        var categories = LoadCategories(connection).ToDictionary(category => category.Id, category => category.Name);

        return LoadRecipes(connection)
            .Where(recipe => recipe.DeletedAt is not null)
            .OrderByDescending(recipe => recipe.DeletedAt)
            .Select(recipe => new DeletedRecipe(
                recipe,
                recipe.CategoryId is { } categoryId && categories.TryGetValue(categoryId, out var categoryName)
                    ? categoryName
                    : null,
                recipe.DeletedAt!.Value))
            .ToList();
    }

    public void DeleteRecipePermanently(Guid recipeId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM recipes WHERE id = $id;";
        command.Parameters.AddWithValue("$id", recipeId.ToString());

        if (command.ExecuteNonQuery() == 0)
        {
            throw new ValidationException("Рецепт не найден.");
        }
    }

    private Dictionary<Guid, decimal?> GetAveragePrices() =>
        _inventory.GetIngredients(includeDeleted: true).ToDictionary(
            summary => summary.Ingredient.Id,
            summary => summary.AveragePricePerUnit);

    private decimal CalculateCost(IReadOnlyList<RecipeLine> lines, IReadOnlyDictionary<Guid, decimal?> prices)
    {
        var cost = 0m;
        foreach (var line in lines)
        {
            cost += line.Amount * (prices.GetValueOrDefault(line.IngredientId) ?? 0m);
        }

        return cost;
    }

    private static List<RecipeLine> CreateLines(List<(Guid IngredientId, decimal Amount)> lines) =>
        lines.Select((line, index) => new RecipeLine(line.IngredientId, line.Amount, index)).ToList();

    private static void InsertRecipe(SqliteConnection connection, SqliteTransaction transaction, Recipe recipe)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO recipes
                (id, name, category_id, output_quantity_milli, output_unit, markup_percent,
                 created_at, updated_at, technology, photo)
            VALUES
                ($id, $name, $category, $quantity, $unit, $markup,
                 $created, $updated, $technology, $photo);
            """;
        command.Parameters.AddWithValue("$id", recipe.Id.ToString());
        command.Parameters.AddWithValue("$name", recipe.Name);
        command.Parameters.AddWithValue("$category", (object?)recipe.CategoryId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$quantity", ToMilli(recipe.OutputQuantity));
        command.Parameters.AddWithValue("$unit", recipe.OutputUnit);
        command.Parameters.AddWithValue("$markup", recipe.MarkupPercent);
        command.Parameters.AddWithValue("$created", recipe.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updated", recipe.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$technology", (object?)recipe.Technology ?? DBNull.Value);
        command.Parameters.AddWithValue("$photo", (object?)recipe.Photo ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static void UpdateRecipeRow(SqliteConnection connection, SqliteTransaction transaction, Recipe recipe)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE recipes
            SET name = $name,
                category_id = $category,
                output_quantity_milli = $quantity,
                output_unit = $unit,
                markup_percent = $markup,
                technology = $technology,
                updated_at = $updated
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", recipe.Id.ToString());
        command.Parameters.AddWithValue("$name", recipe.Name);
        command.Parameters.AddWithValue("$category", (object?)recipe.CategoryId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$quantity", ToMilli(recipe.OutputQuantity));
        command.Parameters.AddWithValue("$unit", recipe.OutputUnit);
        command.Parameters.AddWithValue("$markup", recipe.MarkupPercent);
        command.Parameters.AddWithValue("$technology", (object?)recipe.Technology ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated", recipe.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    private static void InsertLines(SqliteConnection connection, SqliteTransaction transaction, Recipe recipe)
    {
        foreach (var line in recipe.Ingredients)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO recipe_ingredients (recipe_id, ingredient_id, amount_milli, position)
                VALUES ($recipe, $ingredient, $amount, $position);
                """;
            command.Parameters.AddWithValue("$recipe", recipe.Id.ToString());
            command.Parameters.AddWithValue("$ingredient", line.IngredientId.ToString());
            command.Parameters.AddWithValue("$amount", ToMilli(line.Amount));
            command.Parameters.AddWithValue("$position", line.Position);
            command.ExecuteNonQuery();
        }
    }

    private static List<Recipe> LoadRecipes(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, category_id, output_quantity_milli, output_unit, markup_percent,
                   created_at, updated_at, deleted_at, technology, photo
            FROM recipes;
            """;

        var result = new List<Recipe>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(ReadRecipe(reader));
        }

        return result;
    }

    private static Recipe? FindRecipe(SqliteConnection connection, Guid recipeId)
    {
        Recipe? recipe;

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, name, category_id, output_quantity_milli, output_unit, markup_percent,
                       created_at, updated_at, deleted_at, technology, photo
                FROM recipes
                WHERE id = $id AND deleted_at IS NULL
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$id", recipeId.ToString());

            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            recipe = ReadRecipe(reader);
        }

        return recipe with { Ingredients = LoadLines(connection, recipeId) };
    }

    private static Recipe ReadRecipe(SqliteDataReader reader) =>
        new(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
            FromMilli(reader.GetInt64(3)),
            reader.GetString(4),
            reader.GetInt32(5),
            Array.Empty<RecipeLine>(),
            ParseDate(reader.GetString(6)),
            ParseDate(reader.GetString(7)),
            reader.IsDBNull(8) ? null : ParseDate(reader.GetString(8)),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : (byte[])reader[10]);

    private static Dictionary<Guid, List<RecipeLine>> LoadAllLines(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT recipe_id, ingredient_id, amount_milli, position
            FROM recipe_ingredients
            ORDER BY recipe_id, position;
            """;

        var result = new Dictionary<Guid, List<RecipeLine>>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var recipeId = Guid.Parse(reader.GetString(0));
            if (!result.TryGetValue(recipeId, out var list))
            {
                list = new List<RecipeLine>();
                result[recipeId] = list;
            }

            list.Add(new RecipeLine(
                Guid.Parse(reader.GetString(1)),
                FromMilli(reader.GetInt64(2)),
                reader.GetInt32(3)));
        }

        return result;
    }

    private static List<RecipeLine> LoadLines(SqliteConnection connection, Guid recipeId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ingredient_id, amount_milli, position
            FROM recipe_ingredients
            WHERE recipe_id = $recipe
            ORDER BY position;
            """;
        command.Parameters.AddWithValue("$recipe", recipeId.ToString());

        var lines = new List<RecipeLine>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            lines.Add(new RecipeLine(
                Guid.Parse(reader.GetString(0)),
                FromMilli(reader.GetInt64(1)),
                reader.GetInt32(2)));
        }

        return lines;
    }

    private static List<RecipeCategory> LoadCategories(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, name, created_at FROM recipe_categories;";

        var result = new List<RecipeCategory>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new RecipeCategory(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                ParseDate(reader.GetString(2))));
        }

        return result;
    }

    private static Guid? ResolveCategory(SqliteConnection connection, SqliteTransaction transaction, string? categoryName)
    {
        if (string.IsNullOrWhiteSpace(categoryName))
        {
            return null;
        }

        var name = categoryName.Trim();
        var existing = LoadCategories(connection, transaction);
        var found = existing.FirstOrDefault(category =>
            string.Equals(category.Name, name, StringComparison.CurrentCultureIgnoreCase));

        if (found is not null)
        {
            return found.Id;
        }

        var category = new RecipeCategory(Guid.NewGuid(), name, DateTimeOffset.Now);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO recipe_categories (id, name, created_at)
            VALUES ($id, $name, $created);
            """;
        command.Parameters.AddWithValue("$id", category.Id.ToString());
        command.Parameters.AddWithValue("$name", category.Name);
        command.Parameters.AddWithValue("$created", category.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();

        return category.Id;
    }

    private static void EnsureUniqueName(SqliteConnection connection, string name, Guid? exceptId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name FROM recipes;";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = Guid.Parse(reader.GetString(0));
            if (exceptId.HasValue && id == exceptId.Value)
            {
                continue;
            }

            if (string.Equals(reader.GetString(1), name, StringComparison.CurrentCultureIgnoreCase))
            {
                throw new ValidationException($"Рецепт «{name}» уже есть в книге.");
            }
        }
    }

    private static void EnsureIngredientsExist(SqliteConnection connection, List<(Guid IngredientId, decimal Amount)> lines)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM ingredients;";

        var ids = new HashSet<Guid>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                ids.Add(Guid.Parse(reader.GetString(0)));
            }
        }

        foreach (var line in lines)
        {
            if (!ids.Contains(line.IngredientId))
            {
                throw new ValidationException("В рецепте указан неизвестный ингредиент.");
            }
        }
    }

    private static ValidatedInput ValidateInput(
        string? name,
        string? categoryName,
        decimal outputQuantity,
        string? outputUnit,
        int markupPercent,
        IEnumerable<(Guid IngredientId, decimal Amount)> lines)
    {
        var cleanName = (name ?? string.Empty).Trim();
        if (cleanName.Length == 0)
        {
            throw new ValidationException("Укажите название рецепта.");
        }

        if (cleanName.Length > MaxNameLength)
        {
            throw new ValidationException($"Название длиннее {MaxNameLength} символов — сократите.");
        }

        var cleanCategory = (categoryName ?? string.Empty).Trim();
        if (cleanCategory.Length > MaxCategoryLength)
        {
            throw new ValidationException($"Название категории длиннее {MaxCategoryLength} символов — сократите.");
        }

        EnsureCategoryNameAllowed(cleanCategory);

        var quantity = decimal.Round(outputQuantity, 3, MidpointRounding.AwayFromZero);
        if (quantity <= 0m || quantity > MaxOutputQuantity)
        {
            throw new ValidationException($"Выход должен быть больше нуля и не больше {MaxOutputQuantity:0}.");
        }

        var unit = RecipeUnits.Normalize(outputUnit);
        if (!RecipeUnits.IsAllowed(unit))
        {
            throw new ValidationException("Выход измеряется в штуках, килограммах или граммах.");
        }

        if (string.Equals(unit, RecipeUnits.Pieces, StringComparison.Ordinal) && quantity != Math.Truncate(quantity))
        {
            throw new ValidationException("В штуках выход должен быть целым числом.");
        }

        if (markupPercent < 0 || markupPercent > MaxMarkupPercent)
        {
            throw new ValidationException($"Наценка должна быть от 0 до {MaxMarkupPercent}%.");
        }

        var normalizedLines = new List<(Guid IngredientId, decimal Amount)>();
        var seen = new HashSet<Guid>();

        foreach (var line in lines)
        {
            if (line.IngredientId == Guid.Empty)
            {
                throw new ValidationException("Выберите ингредиент в каждой строке.");
            }

            var amount = decimal.Round(line.Amount, 3, MidpointRounding.AwayFromZero);
            if (amount <= 0m || amount > MaxAmount)
            {
                throw new ValidationException("Количество ингредиента должно быть больше нуля.");
            }

            if (!seen.Add(line.IngredientId))
            {
                throw new ValidationException("Один и тот же ингредиент указан в рецепте дважды.");
            }

            normalizedLines.Add((line.IngredientId, amount));
        }

        if (normalizedLines.Count == 0)
        {
            throw new ValidationException("Добавьте в рецепт хотя бы один ингредиент.");
        }

        return new ValidatedInput(
            cleanName,
            cleanCategory.Length == 0 ? null : cleanCategory,
            outputQuantity,
            unit,
            markupPercent,
            normalizedLines);
    }

    private static void EnsureCategoryNameAllowed(string categoryName)
    {
        if (string.Equals(categoryName, "Без категории", StringComparison.CurrentCultureIgnoreCase))
        {
            throw new ValidationException("«Без категории» — служебная группа. Придумайте другое название категории.");
        }
    }

    private static string? CleanTechnology(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length > MaxTechnologyLength)
        {
            throw new ValidationException(
                $"Технология длиннее {MaxTechnologyLength} символов — сократите.");
        }

        return trimmed.Length == 0 ? null : trimmed;
    }

    private static long ToMilli(decimal quantity) =>
        (long)decimal.Round(quantity * 1000m, 0, MidpointRounding.AwayFromZero);

    private static decimal FromMilli(long milli) => milli / 1000m;

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private sealed record ValidatedInput(
        string Name,
        string? CategoryName,
        decimal OutputQuantity,
        string OutputUnit,
        int MarkupPercent,
        List<(Guid IngredientId, decimal Amount)> Lines);
}
