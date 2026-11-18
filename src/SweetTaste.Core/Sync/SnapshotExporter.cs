using System.Globalization;
using Microsoft.Data.Sqlite;
using SweetTaste.Core.Storage;

namespace SweetTaste.Core.Sync;

/// <summary>Читает локальную базу целиком и собирает <see cref="SyncSnapshot"/>.</summary>
/// <remarks>
/// Чтение идёт в одной транзакции, поэтому снапшот согласован: заказы не могут
/// появиться без своих позиций. Настройки устройства и профиль кондитера
/// в снапшот не попадают.
/// </remarks>
public sealed class SnapshotExporter
{
    private readonly AppDatabase _database;

    public SnapshotExporter(AppDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _database.Initialize();
    }

    public SyncSnapshot Export(DateTimeOffset takenAt)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction(deferred: true);

        var snapshot = new SyncSnapshot
        {
            SchemaVersion = DatabaseMigrator.CurrentVersion,
            TakenAt = takenAt,
            IngredientCategories = ReadIngredientCategories(connection, transaction),
            RecipeCategories = ReadRecipeCategories(connection, transaction),
            PackagingCategories = ReadPackagingCategories(connection, transaction),
            Ingredients = ReadIngredients(connection, transaction),
            Purchases = ReadPurchases(connection, transaction),
            Recipes = ReadRecipes(connection, transaction),
            RecipeIngredients = ReadRecipeIngredients(connection, transaction),
            Customers = ReadCustomers(connection, transaction),
            Orders = ReadOrders(connection, transaction),
            OrderItems = ReadOrderItems(connection, transaction),
            PackagingItems = ReadPackagingItems(connection, transaction),
            OrderPackaging = ReadOrderPackaging(connection, transaction),
        };

        transaction.Commit();
        return snapshot;
    }

    private static List<SyncIngredientCategory> ReadIngredientCategories(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = CreateCommand(
            connection,
            transaction,
            "SELECT id, name, created_at FROM ingredient_categories ORDER BY created_at, id;");

        using var reader = command.ExecuteReader();
        var items = new List<SyncIngredientCategory>();
        while (reader.Read())
        {
            items.Add(new SyncIngredientCategory
            {
                Id = ReadGuid(reader, 0),
                Name = reader.GetString(1),
                CreatedAt = ReadTimestamp(reader, 2),
            });
        }

        return items;
    }

    private static List<SyncRecipeCategory> ReadRecipeCategories(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = CreateCommand(
            connection,
            transaction,
            "SELECT id, name, created_at FROM recipe_categories ORDER BY created_at, id;");

        using var reader = command.ExecuteReader();
        var items = new List<SyncRecipeCategory>();
        while (reader.Read())
        {
            items.Add(new SyncRecipeCategory
            {
                Id = ReadGuid(reader, 0),
                Name = reader.GetString(1),
                CreatedAt = ReadTimestamp(reader, 2),
            });
        }

        return items;
    }

    private static List<SyncPackagingCategory> ReadPackagingCategories(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = CreateCommand(
            connection,
            transaction,
            "SELECT id, name, created_at FROM packaging_categories ORDER BY created_at, id;");

        using var reader = command.ExecuteReader();
        var items = new List<SyncPackagingCategory>();
        while (reader.Read())
        {
            items.Add(new SyncPackagingCategory
            {
                Id = ReadGuid(reader, 0),
                Name = reader.GetString(1),
                CreatedAt = ReadTimestamp(reader, 2),
            });
        }

        return items;
    }

    private static List<SyncIngredient> ReadIngredients(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT id, name, brand, unit, category_id, created_at, updated_at, deleted_at
            FROM ingredients
            ORDER BY created_at, id;
            """);

        using var reader = command.ExecuteReader();
        var items = new List<SyncIngredient>();
        while (reader.Read())
        {
            items.Add(new SyncIngredient
            {
                Id = ReadGuid(reader, 0),
                Name = reader.GetString(1),
                Brand = ReadNullableString(reader, 2),
                Unit = reader.GetString(3),
                CategoryId = ReadNullableGuid(reader, 4),
                CreatedAt = ReadTimestamp(reader, 5),
                UpdatedAt = ReadTimestamp(reader, 6),
                DeletedAt = ReadNullableTimestamp(reader, 7),
            });
        }

        return items;
    }

    private static List<SyncPurchase> ReadPurchases(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT id, ingredient_id, purchased_on, store, quantity_milli,
                   total_price_kopecks, order_id, note, created_at
            FROM purchases
            ORDER BY purchased_on, id;
            """);

        using var reader = command.ExecuteReader();
        var items = new List<SyncPurchase>();
        while (reader.Read())
        {
            items.Add(new SyncPurchase
            {
                Id = ReadGuid(reader, 0),
                IngredientId = ReadGuid(reader, 1),
                PurchasedOn = ReadDate(reader, 2),
                Store = ReadNullableString(reader, 3),
                QuantityMilli = reader.GetInt64(4),
                TotalPriceKopecks = reader.GetInt64(5),
                OrderId = ReadNullableGuid(reader, 6),
                Note = ReadNullableString(reader, 7),
                CreatedAt = ReadTimestamp(reader, 8),
            });
        }

        return items;
    }

    private static List<SyncRecipe> ReadRecipes(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT id, name, category_id, output_quantity_milli, output_unit,
                   markup_percent, technology, photo, created_at, updated_at, deleted_at
            FROM recipes
            ORDER BY created_at, id;
            """);

        using var reader = command.ExecuteReader();
        var items = new List<SyncRecipe>();
        while (reader.Read())
        {
            items.Add(new SyncRecipe
            {
                Id = ReadGuid(reader, 0),
                Name = reader.GetString(1),
                CategoryId = ReadNullableGuid(reader, 2),
                OutputQuantityMilli = reader.GetInt64(3),
                OutputUnit = reader.GetString(4),
                MarkupPercent = reader.GetInt32(5),
                Technology = ReadNullableString(reader, 6),
                PhotoBase64 = ReadNullableBlob(reader, 7),
                CreatedAt = ReadTimestamp(reader, 8),
                UpdatedAt = ReadTimestamp(reader, 9),
                DeletedAt = ReadNullableTimestamp(reader, 10),
            });
        }

        return items;
    }

    private static List<SyncRecipeIngredient> ReadRecipeIngredients(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT recipe_id, ingredient_id, amount_milli, position
            FROM recipe_ingredients
            ORDER BY recipe_id, position;
            """);

        using var reader = command.ExecuteReader();
        var items = new List<SyncRecipeIngredient>();
        while (reader.Read())
        {
            items.Add(new SyncRecipeIngredient
            {
                RecipeId = ReadGuid(reader, 0),
                IngredientId = ReadGuid(reader, 1),
                AmountMilli = reader.GetInt64(2),
                Position = reader.GetInt32(3),
            });
        }

        return items;
    }

    private static List<SyncCustomer> ReadCustomers(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT id, number, first_name, last_name, phone, phone_normalized,
                   created_at, updated_at
            FROM customers
            ORDER BY number;
            """);

        using var reader = command.ExecuteReader();
        var items = new List<SyncCustomer>();
        while (reader.Read())
        {
            items.Add(new SyncCustomer
            {
                Id = ReadGuid(reader, 0),
                Number = reader.GetInt32(1),
                FirstName = reader.GetString(2),
                LastName = ReadNullableString(reader, 3),
                Phone = reader.GetString(4),
                PhoneNormalized = reader.GetString(5),
                CreatedAt = ReadTimestamp(reader, 6),
                UpdatedAt = ReadTimestamp(reader, 7),
            });
        }

        return items;
    }

    private static List<SyncOrder> ReadOrders(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT id, number, first_name, last_name, phone, due_date, status, comment,
                   total_price_kopecks, paid_amount_kopecks, discount_kopecks, customer_id,
                   created_at, updated_at
            FROM orders
            ORDER BY number;
            """);

        using var reader = command.ExecuteReader();
        var items = new List<SyncOrder>();
        while (reader.Read())
        {
            items.Add(new SyncOrder
            {
                Id = ReadGuid(reader, 0),
                Number = reader.GetInt32(1),
                FirstName = reader.GetString(2),
                LastName = ReadNullableString(reader, 3),
                Phone = reader.GetString(4),
                DueDate = ReadDate(reader, 5),
                Status = reader.GetString(6),
                Comment = ReadNullableString(reader, 7),
                TotalPriceKopecks = reader.GetInt64(8),
                PaidAmountKopecks = reader.GetInt64(9),
                DiscountKopecks = reader.GetInt64(10),
                CustomerId = ReadNullableGuid(reader, 11),
                CreatedAt = ReadTimestamp(reader, 12),
                UpdatedAt = ReadTimestamp(reader, 13),
            });
        }

        return items;
    }

    private static List<SyncOrderItem> ReadOrderItems(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT id, order_id, recipe_id, recipe_name, quantity_milli,
                   unit_price_kopecks, cost_price_kopecks, cost_estimated,
                   output_quantity_milli, output_unit
            FROM order_items
            ORDER BY order_id, id;
            """);

        using var reader = command.ExecuteReader();
        var items = new List<SyncOrderItem>();
        while (reader.Read())
        {
            items.Add(new SyncOrderItem
            {
                Id = ReadGuid(reader, 0),
                OrderId = ReadGuid(reader, 1),
                RecipeId = ReadNullableGuid(reader, 2),
                RecipeName = reader.GetString(3),
                QuantityMilli = reader.GetInt64(4),
                UnitPriceKopecks = reader.GetInt64(5),
                CostPriceKopecks = reader.GetInt64(6),
                CostEstimated = reader.GetInt64(7) != 0,
                OutputQuantityMilli = reader.GetInt64(8),
                OutputUnit = reader.GetString(9),
            });
        }

        return items;
    }

    private static List<SyncPackagingItem> ReadPackagingItems(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT id, category_id, name, size, price_kopecks, created_at, updated_at
            FROM packaging_items
            ORDER BY created_at, id;
            """);

        using var reader = command.ExecuteReader();
        var items = new List<SyncPackagingItem>();
        while (reader.Read())
        {
            items.Add(new SyncPackagingItem
            {
                Id = ReadGuid(reader, 0),
                CategoryId = ReadNullableGuid(reader, 1),
                Name = reader.GetString(2),
                Size = ReadNullableString(reader, 3),
                PriceKopecks = reader.GetInt64(4),
                CreatedAt = ReadTimestamp(reader, 5),
                UpdatedAt = ReadTimestamp(reader, 6),
            });
        }

        return items;
    }

    private static List<SyncOrderPackaging> ReadOrderPackaging(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT id, order_id, packaging_id, name, size, cost_kopecks,
                   sale_price_kopecks, quantity, position
            FROM order_packaging
            ORDER BY order_id, position;
            """);

        using var reader = command.ExecuteReader();
        var items = new List<SyncOrderPackaging>();
        while (reader.Read())
        {
            items.Add(new SyncOrderPackaging
            {
                Id = ReadGuid(reader, 0),
                OrderId = ReadGuid(reader, 1),
                PackagingId = ReadNullableGuid(reader, 2),
                Name = reader.GetString(3),
                Size = ReadNullableString(reader, 4),
                CostKopecks = reader.GetInt64(5),
                SalePriceKopecks = reader.GetInt64(6),
                Quantity = reader.GetInt32(7),
                Position = reader.GetInt32(8),
            });
        }

        return items;
    }

    private static SqliteCommand CreateCommand(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static Guid ReadGuid(SqliteDataReader reader, int ordinal) =>
        Guid.Parse(reader.GetString(ordinal));

    private static Guid? ReadNullableGuid(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Guid.Parse(reader.GetString(ordinal));

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset ReadTimestamp(SqliteDataReader reader, int ordinal) =>
        DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture);

    private static DateTimeOffset? ReadNullableTimestamp(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture);

    private static DateOnly ReadDate(SqliteDataReader reader, int ordinal) =>
        DateOnly.ParseExact(reader.GetString(ordinal), "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string? ReadNullableBlob(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var bytes = (byte[])reader.GetValue(ordinal);
        return Convert.ToBase64String(bytes);
    }
}
