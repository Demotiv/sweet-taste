using System.Globalization;
using Microsoft.Data.Sqlite;
using SweetTaste.Core.Domain;
using SweetTaste.Core.Storage;

namespace SweetTaste.Core.Services;

/// <summary>
/// Справочник ингредиентов и журнал закупок.
/// Средняя цена считается только здесь, UI её не пересчитывает.
/// </summary>
public sealed class InventoryService
{
    private const int MaxNameLength = 80;
    private const int MaxCategoryLength = 60;
    private const int MaxStoreLength = 80;
    private const int MaxNoteLength = 300;
    private const decimal MaxQuantity = 1_000_000m;
    private const decimal MaxPrice = 10_000_000m;

    private readonly AppDatabase _database;

    public InventoryService(AppDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _database.Initialize();
    }

    public AppDatabase Database => _database;

    public IReadOnlyList<IngredientSummary> GetIngredients(bool includeDeleted = false)
    {
        using var connection = _database.Open();
        var ingredients = LoadIngredients(connection)
            .Where(ingredient => includeDeleted || ingredient.DeletedAt is null)
            .ToList();
        var categories = LoadCategories(connection).ToDictionary(category => category.Id, category => category.Name);
        var purchases = LoadPurchases(connection);
        var byIngredient = purchases
            .GroupBy(purchase => purchase.IngredientId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var result = new List<IngredientSummary>(ingredients.Count);

        foreach (var ingredient in ingredients)
        {
            byIngredient.TryGetValue(ingredient.Id, out var list);
            list ??= new List<Purchase>();

            var ordered = Order(list);
            var totalQuantity = ordered.Sum(purchase => purchase.Quantity);
            var totalSpent = ordered.Sum(purchase => purchase.TotalPrice);

            result.Add(new IngredientSummary(
                ingredient,
                ingredient.CategoryId is { } categoryId && categories.TryGetValue(categoryId, out var categoryName)
                    ? categoryName
                    : null,
                AveragePrice(totalSpent, totalQuantity),
                ordered.Count,
                totalQuantity,
                totalSpent,
                ordered.FirstOrDefault()));
        }

        return result
            .OrderBy(summary => summary.CategoryName is null ? 1 : 0)
            .ThenBy(summary => summary.CategoryName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(summary => summary.Ingredient.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public IngredientDetails GetDetails(Guid ingredientId)
    {
        using var connection = _database.Open();
        var ingredient = FindIngredient(connection, ingredientId)
            ?? throw new ValidationException("Ингредиент не найден.");

        var purchases = Order(LoadPurchases(connection, ingredientId));
        var totalQuantity = purchases.Sum(purchase => purchase.Quantity);
        var totalSpent = purchases.Sum(purchase => purchase.TotalPrice);

        var categoryName = ingredient.CategoryId is { } categoryId
            ? LoadCategories(connection).FirstOrDefault(category => category.Id == categoryId)?.Name
            : null;

        return new IngredientDetails(
            ingredient,
            categoryName,
            purchases,
            AveragePrice(totalSpent, totalQuantity),
            totalQuantity,
            totalSpent);
    }

    public InventoryOverview GetOverview(DateOnly today)
    {
        using var connection = _database.Open();
        var ingredients = LoadIngredients(connection);
        var purchases = LoadPurchases(connection);

        var thisMonth = purchases
            .Where(purchase => purchase.PurchasedOn.Year == today.Year && purchase.PurchasedOn.Month == today.Month)
            .ToList();

        var largest = thisMonth
            .OrderByDescending(purchase => purchase.TotalPrice)
            .ThenByDescending(purchase => purchase.PurchasedOn)
            .FirstOrDefault();

        var largestName = largest is null
            ? null
            : ingredients.FirstOrDefault(ingredient => ingredient.Id == largest.IngredientId)?.Name;

        return new InventoryOverview(
            ingredients.Count(ingredient => ingredient.DeletedAt is null),
            purchases.Count,
            thisMonth.Sum(purchase => purchase.TotalPrice),
            thisMonth.Count,
            largest,
            largestName);
    }

    public Ingredient AddIngredient(string name, string? brand, string unit, string? categoryName = null)
    {
        var cleanName = RequireName(name);
        var cleanBrand = CleanOptional(brand, MaxNameLength, "Бренд");
        var cleanUnit = RequireUnit(unit);
        var cleanCategory = CleanCategory(categoryName);

        using var connection = _database.Open();
        EnsureUniqueName(connection, cleanName, exceptId: null);

        using var transaction = connection.BeginTransaction();
        var categoryId = ResolveCategory(connection, transaction, cleanCategory);

        var now = DateTimeOffset.Now;
        var ingredient = new Ingredient(
            Guid.NewGuid(),
            cleanName,
            cleanBrand,
            cleanUnit,
            categoryId,
            now,
            now,
            DeletedAt: null);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ingredients (id, name, brand, unit, category_id, created_at, updated_at)
            VALUES ($id, $name, $brand, $unit, $category, $created, $updated);
            """;
        command.Parameters.AddWithValue("$id", ingredient.Id.ToString());
        command.Parameters.AddWithValue("$name", ingredient.Name);
        command.Parameters.AddWithValue("$brand", (object?)ingredient.Brand ?? DBNull.Value);
        command.Parameters.AddWithValue("$unit", ingredient.Unit);
        command.Parameters.AddWithValue("$category", (object?)ingredient.CategoryId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", ingredient.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updated", ingredient.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();

        transaction.Commit();
        return ingredient;
    }

    public Ingredient UpdateIngredient(Guid id, string name, string? brand, string unit, string? categoryName = null)
    {
        var cleanName = RequireName(name);
        var cleanBrand = CleanOptional(brand, MaxNameLength, "Бренд");
        var cleanUnit = RequireUnit(unit);
        var cleanCategory = CleanCategory(categoryName);

        using var connection = _database.Open();
        var existing = FindIngredient(connection, id)
            ?? throw new ValidationException("Ингредиент не найден.");

        EnsureUniqueName(connection, cleanName, exceptId: id);

        if (!string.Equals(existing.Unit, cleanUnit, StringComparison.Ordinal) &&
            (CountPurchases(connection, id) > 0 || FindRecipeNamesUsingIngredient(connection, id).Count > 0))
        {
            throw new ValidationException(
                "Нельзя сменить единицу измерения: у ингредиента есть закупки или он используется в рецептах.");
        }

        using var transaction = connection.BeginTransaction();
        var categoryId = ResolveCategory(connection, transaction, cleanCategory);

        var updated = existing with
        {
            Name = cleanName,
            Brand = cleanBrand,
            Unit = cleanUnit,
            CategoryId = categoryId,
            UpdatedAt = DateTimeOffset.Now,
        };

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE ingredients
            SET name = $name, brand = $brand, unit = $unit, category_id = $category, updated_at = $updated
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", updated.Id.ToString());
        command.Parameters.AddWithValue("$name", updated.Name);
        command.Parameters.AddWithValue("$brand", (object?)updated.Brand ?? DBNull.Value);
        command.Parameters.AddWithValue("$unit", updated.Unit);
        command.Parameters.AddWithValue("$category", (object?)updated.CategoryId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated", updated.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();

        transaction.Commit();
        return updated;
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
        command.CommandText = "DELETE FROM ingredient_categories WHERE id = $id;";
        command.Parameters.AddWithValue("$id", category.Id.ToString());
        command.ExecuteNonQuery();
    }

    public void RenameCategory(string oldName, string newName)
    {
        var oldClean = (oldName ?? string.Empty).Trim();
        var newClean = CleanCategory(newName)
            ?? throw new ValidationException("Укажите новое название категории.");

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
        command.CommandText = "UPDATE ingredient_categories SET name = $name WHERE id = $id;";
        command.Parameters.AddWithValue("$id", category.Id.ToString());
        command.Parameters.AddWithValue("$name", newClean);
        command.ExecuteNonQuery();
    }

    public void DeleteIngredient(Guid id, bool force = false)
    {
        if (force)
        {
            DeleteIngredientPermanently(id);
            return;
        }

        using var connection = _database.Open();
        var ingredient = FindIngredient(connection, id)
            ?? throw new ValidationException("Ингредиент не найден.");

        if (CountPurchases(connection, id) > 0)
        {
            throw new ValidationException(
                "У ингредиента есть закупки — они часть истории цен. Сначала удалите закупки.");
        }

        var usedInRecipes = FindRecipeNamesUsingIngredient(connection, id);
        if (usedInRecipes.Count > 0)
        {
            throw new ValidationException(
                $"«{ingredient.Name}» используется в рецептах: {string.Join(", ", usedInRecipes)}. Сначала уберите его из состава.");
        }

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ingredients WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        command.ExecuteNonQuery();
    }

    public void MoveIngredientToTrash(Guid id)
    {
        using var connection = _database.Open();
        var ingredient = FindIngredient(connection, id)
            ?? throw new ValidationException("Ингредиент не найден.");

        if (ingredient.DeletedAt is not null)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ingredients
            SET deleted_at = $deleted, updated_at = $updated
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$deleted", now.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updated", now.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public void RestoreIngredient(Guid id)
    {
        using var connection = _database.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ingredients
            SET deleted_at = NULL, updated_at = $updated
            WHERE id = $id AND deleted_at IS NOT NULL;
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$updated", DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));

        if (command.ExecuteNonQuery() == 0)
        {
            throw new ValidationException("Ингредиент в корзине не найден.");
        }
    }

    public IReadOnlyList<DeletedIngredient> GetDeletedIngredients()
    {
        using var connection = _database.Open();
        var ingredients = LoadIngredients(connection)
            .Where(ingredient => ingredient.DeletedAt is not null)
            .ToList();

        if (ingredients.Count == 0)
        {
            return Array.Empty<DeletedIngredient>();
        }

        var categories = LoadCategories(connection).ToDictionary(category => category.Id, category => category.Name);
        var purchaseCounts = LoadPurchases(connection)
            .GroupBy(purchase => purchase.IngredientId)
            .ToDictionary(group => group.Key, group => group.Count());

        return ingredients
            .OrderByDescending(ingredient => ingredient.DeletedAt)
            .Select(ingredient => new DeletedIngredient(
                ingredient,
                ingredient.CategoryId is { } categoryId && categories.TryGetValue(categoryId, out var categoryName)
                    ? categoryName
                    : null,
                ingredient.DeletedAt!.Value,
                purchaseCounts.GetValueOrDefault(ingredient.Id)))
            .ToList();
    }

    public void DeleteIngredientPermanently(Guid id)
    {
        using var connection = _database.Open();
        _ = FindIngredient(connection, id) ?? throw new ValidationException("Ингредиент не найден.");

        using var transaction = connection.BeginTransaction();

        using (var deletePurchases = connection.CreateCommand())
        {
            deletePurchases.Transaction = transaction;
            deletePurchases.CommandText = "DELETE FROM purchases WHERE ingredient_id = $id;";
            deletePurchases.Parameters.AddWithValue("$id", id.ToString());
            deletePurchases.ExecuteNonQuery();
        }

        using (var deleteRecipeLines = connection.CreateCommand())
        {
            deleteRecipeLines.Transaction = transaction;
            deleteRecipeLines.CommandText = "DELETE FROM recipe_ingredients WHERE ingredient_id = $id;";
            deleteRecipeLines.Parameters.AddWithValue("$id", id.ToString());
            deleteRecipeLines.ExecuteNonQuery();
        }

        using (var deleteIngredient = connection.CreateCommand())
        {
            deleteIngredient.Transaction = transaction;
            deleteIngredient.CommandText = "DELETE FROM ingredients WHERE id = $id;";
            deleteIngredient.Parameters.AddWithValue("$id", id.ToString());
            deleteIngredient.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public Purchase AddPurchase(
        Guid ingredientId,
        DateOnly purchasedOn,
        string? store,
        decimal quantity,
        decimal totalPrice,
        Guid? orderId = null,
        string? note = null)
    {
        var normalized = ValidatePurchaseInput(purchasedOn, store, quantity, totalPrice, note);

        using var connection = _database.Open();
        _ = FindIngredient(connection, ingredientId) ?? throw new ValidationException("Ингредиент не найден.");

        var purchase = new Purchase(
            Guid.NewGuid(),
            ingredientId,
            purchasedOn,
            normalized.Store,
            normalized.Quantity,
            normalized.TotalPrice,
            orderId,
            normalized.Note,
            DateTimeOffset.Now);

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO purchases
                (id, ingredient_id, purchased_on, store, quantity_milli, total_price_kopecks, order_id, note, created_at)
            VALUES
                ($id, $ingredient, $date, $store, $quantity, $price, $order, $note, $created);
            """;
        command.Parameters.AddWithValue("$id", purchase.Id.ToString());
        command.Parameters.AddWithValue("$ingredient", purchase.IngredientId.ToString());
        command.Parameters.AddWithValue("$date", purchase.PurchasedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$store", (object?)purchase.Store ?? DBNull.Value);
        command.Parameters.AddWithValue("$quantity", ToMilli(purchase.Quantity));
        command.Parameters.AddWithValue("$price", ToKopecks(purchase.TotalPrice));
        command.Parameters.AddWithValue("$order", (object?)purchase.OrderId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$note", (object?)purchase.Note ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", purchase.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();

        return purchase;
    }

    public Purchase UpdatePurchase(
        Guid purchaseId,
        DateOnly purchasedOn,
        string? store,
        decimal quantity,
        decimal totalPrice,
        string? note = null)
    {
        var normalized = ValidatePurchaseInput(purchasedOn, store, quantity, totalPrice, note);

        using var connection = _database.Open();
        var existing = FindPurchase(connection, purchaseId)
            ?? throw new ValidationException("Закупка не найдена.");

        var updated = existing with
        {
            PurchasedOn = purchasedOn,
            Store = normalized.Store,
            Quantity = normalized.Quantity,
            TotalPrice = normalized.TotalPrice,
            Note = normalized.Note,
        };

        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE purchases
            SET purchased_on = $date,
                store = $store,
                quantity_milli = $quantity,
                total_price_kopecks = $price,
                note = $note
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", updated.Id.ToString());
        command.Parameters.AddWithValue("$date", updated.PurchasedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$store", (object?)updated.Store ?? DBNull.Value);
        command.Parameters.AddWithValue("$quantity", ToMilli(updated.Quantity));
        command.Parameters.AddWithValue("$price", ToKopecks(updated.TotalPrice));
        command.Parameters.AddWithValue("$note", (object?)updated.Note ?? DBNull.Value);
        command.ExecuteNonQuery();

        return updated;
    }

    public void DeletePurchase(Guid purchaseId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM purchases WHERE id = $id;";
        command.Parameters.AddWithValue("$id", purchaseId.ToString());

        if (command.ExecuteNonQuery() == 0)
        {
            throw new ValidationException("Закупка не найдена.");
        }
    }

    /// <summary>Закупки за период (включительно), по возрастанию даты.</summary>
    public IReadOnlyList<Purchase> GetPurchases(DateOnly from, DateOnly to)
    {
        if (to < from)
        {
            throw new ValidationException("Конец периода раньше начала.");
        }

        using var connection = _database.Open();
        return LoadPurchases(connection)
            .Where(purchase => purchase.PurchasedOn >= from && purchase.PurchasedOn <= to)
            .OrderBy(purchase => purchase.PurchasedOn)
            .ThenBy(purchase => purchase.CreatedAt)
            .ToList();
    }

    private static List<Purchase> Order(IEnumerable<Purchase> purchases) =>
        purchases
            .OrderByDescending(purchase => purchase.PurchasedOn)
            .ThenByDescending(purchase => purchase.CreatedAt)
            .ToList();

    private static decimal? AveragePrice(decimal totalSpent, decimal totalQuantity) =>
        totalQuantity > 0m ? totalSpent / totalQuantity : null;

    private static List<Ingredient> LoadIngredients(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, brand, unit, category_id, created_at, updated_at, deleted_at
            FROM ingredients
            ORDER BY name;
            """;

        var result = new List<Ingredient>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(ReadIngredient(reader));
        }

        return result;
    }

    private static List<Purchase> LoadPurchases(SqliteConnection connection, Guid? ingredientId = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, ingredient_id, purchased_on, store, quantity_milli, total_price_kopecks, order_id, note, created_at
            FROM purchases
            WHERE $filter IS NULL OR ingredient_id = $ingredient
            ORDER BY purchased_on DESC, created_at DESC;
            """;
        command.Parameters.AddWithValue("$filter", ingredientId.HasValue ? 1 : (object)DBNull.Value);
        command.Parameters.AddWithValue("$ingredient", (object?)ingredientId?.ToString() ?? DBNull.Value);

        var result = new List<Purchase>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(ReadPurchase(reader));
        }

        return result;
    }

    private static Purchase? FindPurchase(SqliteConnection connection, Guid id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, ingredient_id, purchased_on, store, quantity_milli, total_price_kopecks, order_id, note, created_at
            FROM purchases
            WHERE id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", id.ToString());

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadPurchase(reader) : null;
    }

    private static Purchase ReadPurchase(SqliteDataReader reader) =>
        new(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            DateOnly.ParseExact(reader.GetString(2), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            FromMilli(reader.GetInt64(4)),
            FromKopecks(reader.GetInt64(5)),
            reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6)),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            ParseDate(reader.GetString(8)));

    private static Ingredient? FindIngredient(SqliteConnection connection, Guid id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, brand, unit, category_id, created_at, updated_at, deleted_at
            FROM ingredients
            WHERE id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", id.ToString());

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadIngredient(reader) : null;
    }

    private static Ingredient ReadIngredient(SqliteDataReader reader) =>
        new(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4)),
            ParseDate(reader.GetString(5)),
            ParseDate(reader.GetString(6)),
            reader.IsDBNull(7) ? null : ParseDate(reader.GetString(7)));

    private static void EnsureUniqueName(SqliteConnection connection, string name, Guid? exceptId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name FROM ingredients;";

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
                throw new ValidationException($"Ингредиент «{name}» уже есть в справочнике.");
            }
        }
    }

    private static long CountPurchases(SqliteConnection connection, Guid ingredientId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM purchases WHERE ingredient_id = $id;";
        command.Parameters.AddWithValue("$id", ingredientId.ToString());
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static List<string> FindRecipeNamesUsingIngredient(SqliteConnection connection, Guid ingredientId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.name
            FROM recipe_ingredients ri
            JOIN recipes r ON r.id = ri.recipe_id
            WHERE ri.ingredient_id = $id
            ORDER BY r.name;
            """;
        command.Parameters.AddWithValue("$id", ingredientId.ToString());

        var names = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static List<IngredientCategory> LoadCategories(
        SqliteConnection connection,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, name, created_at FROM ingredient_categories;";

        var result = new List<IngredientCategory>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new IngredientCategory(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                ParseDate(reader.GetString(2))));
        }

        return result;
    }

    private static Guid? ResolveCategory(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? categoryName)
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

        var category = new IngredientCategory(Guid.NewGuid(), name, DateTimeOffset.Now);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ingredient_categories (id, name, created_at)
            VALUES ($id, $name, $created);
            """;
        command.Parameters.AddWithValue("$id", category.Id.ToString());
        command.Parameters.AddWithValue("$name", category.Name);
        command.Parameters.AddWithValue("$created", category.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();

        return category.Id;
    }

    private static string? CleanCategory(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (trimmed.Length > MaxCategoryLength)
        {
            throw new ValidationException($"Название категории длиннее {MaxCategoryLength} символов — сократите.");
        }

        if (string.Equals(trimmed, "Без категории", StringComparison.CurrentCultureIgnoreCase))
        {
            throw new ValidationException("«Без категории» — служебная группа. Придумайте другое название категории.");
        }

        return trimmed;
    }

    private static long ToKopecks(decimal rubles) =>
        (long)decimal.Round(rubles * 100m, 0, MidpointRounding.AwayFromZero);

    private static decimal FromKopecks(long kopecks) => kopecks / 100m;

    private static long ToMilli(decimal quantity) =>
        (long)decimal.Round(quantity * 1000m, 0, MidpointRounding.AwayFromZero);

    private static decimal FromMilli(long milli) => milli / 1000m;

    private static string RequireName(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new ValidationException("Укажите название ингредиента.");
        }

        if (trimmed.Length > MaxNameLength)
        {
            throw new ValidationException($"Название длиннее {MaxNameLength} символов — сократите.");
        }

        return trimmed;
    }

    private static (decimal Quantity, decimal TotalPrice, string? Store, string? Note) ValidatePurchaseInput(
        DateOnly purchasedOn,
        string? store,
        decimal quantity,
        decimal totalPrice,
        string? note)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (purchasedOn > today)
        {
            throw new ValidationException("Дата закупки не может быть в будущем.");
        }

        quantity = decimal.Round(quantity, 3, MidpointRounding.AwayFromZero);
        totalPrice = decimal.Round(totalPrice, 2, MidpointRounding.AwayFromZero);

        if (quantity <= 0m || quantity > MaxQuantity)
        {
            throw new ValidationException("Количество должно быть больше нуля.");
        }

        if (totalPrice < 0m || totalPrice > MaxPrice)
        {
            throw new ValidationException("Сумма закупки указана неверно.");
        }

        return (
            quantity,
            totalPrice,
            CleanOptional(store, MaxStoreLength, "Магазин"),
            CleanOptional(note, MaxNoteLength, "Заметка"));
    }

    private static string RequireUnit(string? unit)
    {
        var normalized = Units.Normalize(unit);
        if (!Units.IsAllowed(normalized))
        {
            throw new ValidationException("Единица измерения должна быть одной из: г, мл, шт.");
        }

        return normalized;
    }

    private static string? CleanOptional(string? value, int maxLength, string field)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (trimmed.Length > maxLength)
        {
            throw new ValidationException($"{field}: не длиннее {maxLength} символов.");
        }

        return trimmed;
    }
}
