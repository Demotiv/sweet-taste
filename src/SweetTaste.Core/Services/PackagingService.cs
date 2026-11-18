using System.Globalization;
using Microsoft.Data.Sqlite;
using SweetTaste.Core.Domain;
using SweetTaste.Core.Storage;

namespace SweetTaste.Core.Services;

/// <summary>Справочник упаковки: категории коробок, названия, размеры и закупочные цены.</summary>
public sealed class PackagingService
{
    private const int MaxNameLength = 80;
    private const int MaxSizeLength = 40;
    private const int MaxCategoryLength = 60;
    private const decimal MaxPrice = 10_000_000m;

    private readonly AppDatabase _database;

    public PackagingService(AppDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _database.Initialize();
    }

    public IReadOnlyList<PackagingSummary> GetItems()
    {
        using var connection = _database.Open();
        var categories = LoadCategories(connection).ToDictionary(category => category.Id, category => category.Name);

        return LoadItems(connection)
            .Select(item => new PackagingSummary(
                item,
                item.CategoryId is { } categoryId && categories.TryGetValue(categoryId, out var name) ? name : null))
            .OrderBy(summary => summary.CategoryName is null ? 1 : 0)
            .ThenBy(summary => summary.CategoryName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(summary => summary.Item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<PackagingCategory> GetCategories()
    {
        using var connection = _database.Open();
        return LoadCategories(connection)
            .OrderBy(category => category.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public PackagingCategory CreateCategory(string name)
    {
        var cleanName = CleanCategoryName(name);

        using var connection = _database.Open();
        var existing = LoadCategories(connection).FirstOrDefault(category =>
            string.Equals(category.Name, cleanName, StringComparison.CurrentCultureIgnoreCase));

        if (existing is not null)
        {
            return existing;
        }

        var created = new PackagingCategory(Guid.NewGuid(), cleanName, DateTimeOffset.Now);

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO packaging_categories (id, name, created_at)
            VALUES ($id, $name, $created);
            """;
        command.Parameters.AddWithValue("$id", created.Id.ToString());
        command.Parameters.AddWithValue("$name", created.Name);
        command.Parameters.AddWithValue("$created", created.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();

        return created;
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
        command.CommandText = "DELETE FROM packaging_categories WHERE id = $id;";
        command.Parameters.AddWithValue("$id", category.Id.ToString());
        command.ExecuteNonQuery();
    }

    public PackagingItem CreateItem(string name, string? size, decimal price, string? categoryName)
    {
        var validated = ValidateItem(name, size, price);

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        var categoryId = ResolveCategory(connection, transaction, categoryName);

        var now = DateTimeOffset.Now;
        var item = new PackagingItem(
            Guid.NewGuid(),
            categoryId,
            validated.Name,
            validated.Size,
            validated.Price,
            now,
            now);

        InsertItem(connection, transaction, item);
        transaction.Commit();
        return item;
    }

    public PackagingItem UpdateItem(Guid id, string name, string? size, decimal price, string? categoryName)
    {
        var validated = ValidateItem(name, size, price);

        using var connection = _database.Open();
        var existing = FindItem(connection, id)
            ?? throw new ValidationException("Упаковка не найдена.");

        using var transaction = connection.BeginTransaction();
        var categoryId = ResolveCategory(connection, transaction, categoryName);

        var updated = existing with
        {
            CategoryId = categoryId,
            Name = validated.Name,
            Size = validated.Size,
            Price = validated.Price,
            UpdatedAt = DateTimeOffset.Now,
        };

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE packaging_items
                SET category_id = $category,
                    name = $name,
                    size = $size,
                    price_kopecks = $price,
                    updated_at = $updated
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", updated.Id.ToString());
            command.Parameters.AddWithValue("$category", (object?)updated.CategoryId?.ToString() ?? DBNull.Value);
            command.Parameters.AddWithValue("$name", updated.Name);
            command.Parameters.AddWithValue("$size", (object?)updated.Size ?? DBNull.Value);
            command.Parameters.AddWithValue("$price", ToKopecks(updated.Price));
            command.Parameters.AddWithValue("$updated", updated.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        return updated;
    }

    public void DeleteItem(Guid id)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM packaging_items WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());

        if (command.ExecuteNonQuery() == 0)
        {
            throw new ValidationException("Упаковка не найдена.");
        }
    }

    private static void InsertItem(SqliteConnection connection, SqliteTransaction transaction, PackagingItem item)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO packaging_items
                (id, category_id, name, size, price_kopecks, created_at, updated_at)
            VALUES
                ($id, $category, $name, $size, $price, $created, $updated);
            """;
        command.Parameters.AddWithValue("$id", item.Id.ToString());
        command.Parameters.AddWithValue("$category", (object?)item.CategoryId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$name", item.Name);
        command.Parameters.AddWithValue("$size", (object?)item.Size ?? DBNull.Value);
        command.Parameters.AddWithValue("$price", ToKopecks(item.Price));
        command.Parameters.AddWithValue("$created", item.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updated", item.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    private static List<PackagingItem> LoadItems(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, category_id, name, size, price_kopecks, created_at, updated_at
            FROM packaging_items;
            """;

        var result = new List<PackagingItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(ReadItem(reader));
        }

        return result;
    }

    private static PackagingItem? FindItem(SqliteConnection connection, Guid id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, category_id, name, size, price_kopecks, created_at, updated_at
            FROM packaging_items
            WHERE id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", id.ToString());

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadItem(reader) : null;
    }

    private static PackagingItem ReadItem(SqliteDataReader reader) =>
        new(
            Guid.Parse(reader.GetString(0)),
            reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            FromKopecks(reader.GetInt64(4)),
            ParseDate(reader.GetString(5)),
            ParseDate(reader.GetString(6)));

    private static List<PackagingCategory> LoadCategories(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, created_at FROM packaging_categories;";

        var result = new List<PackagingCategory>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new PackagingCategory(
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

        var name = CleanCategoryName(categoryName);
        var found = LoadCategories(connection).FirstOrDefault(category =>
            string.Equals(category.Name, name, StringComparison.CurrentCultureIgnoreCase));

        if (found is not null)
        {
            return found.Id;
        }

        var created = new PackagingCategory(Guid.NewGuid(), name, DateTimeOffset.Now);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO packaging_categories (id, name, created_at)
            VALUES ($id, $name, $created);
            """;
        command.Parameters.AddWithValue("$id", created.Id.ToString());
        command.Parameters.AddWithValue("$name", created.Name);
        command.Parameters.AddWithValue("$created", created.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();

        return created.Id;
    }

    private static (string Name, string? Size, decimal Price) ValidateItem(
        string? name,
        string? size,
        decimal price)
    {
        var cleanName = (name ?? string.Empty).Trim();
        if (cleanName.Length == 0)
        {
            throw new ValidationException("Укажите название упаковки.");
        }

        if (cleanName.Length > MaxNameLength)
        {
            throw new ValidationException($"Название длиннее {MaxNameLength} символов — сократите.");
        }

        var cleanSize = (size ?? string.Empty).Trim();
        if (cleanSize.Length > MaxSizeLength)
        {
            throw new ValidationException($"Размер длиннее {MaxSizeLength} символов — сократите.");
        }

        var cleanPrice = decimal.Round(price, 2, MidpointRounding.AwayFromZero);
        if (cleanPrice < 0m || cleanPrice > MaxPrice)
        {
            throw new ValidationException("Цена упаковки указана неверно.");
        }

        return (cleanName, cleanSize.Length == 0 ? null : cleanSize, cleanPrice);
    }

    private static string CleanCategoryName(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new ValidationException("Укажите название категории упаковки.");
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

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
