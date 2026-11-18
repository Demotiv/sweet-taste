using System.Globalization;
using Microsoft.Data.Sqlite;
using SweetTaste.Core.Storage;

namespace SweetTaste.Core.Sync;

/// <summary>Записывает снапшот из облака в локальную базу.</summary>
/// <remarks>
/// Замена идёт в одной транзакции: сначала удаляются все строки бизнес-данных
/// в обратном порядке зависимостей, затем вставляются строки снапшота в прямом.
/// Настройки устройства и профиль кондитера не трогаются — они локальные.
/// <c>profile_id</c> подставляется единицей: профиль пока один.
/// </remarks>
public sealed class SnapshotImporter
{
    private const int ProfileId = 1;

    private readonly AppDatabase _database;

    public SnapshotImporter(AppDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _database.Initialize();
    }

    /// <summary>Заменяет бизнес-данные локальной базы снапшотом. Возвращает число строк.</summary>
    public int Import(SyncSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        foreach (var table in SnapshotRows.LoadOrder.Reverse())
        {
            DeleteAll(connection, transaction, table);
        }

        InsertIngredientCategories(connection, transaction, snapshot.IngredientCategories);
        InsertRecipeCategories(connection, transaction, snapshot.RecipeCategories);
        InsertPackagingCategories(connection, transaction, snapshot.PackagingCategories);
        InsertIngredients(connection, transaction, snapshot.Ingredients);
        InsertPurchases(connection, transaction, snapshot.Purchases);
        InsertRecipes(connection, transaction, snapshot.Recipes);
        InsertRecipeIngredients(connection, transaction, snapshot.RecipeIngredients);
        InsertCustomers(connection, transaction, snapshot.Customers);
        InsertOrders(connection, transaction, snapshot.Orders);
        InsertOrderItems(connection, transaction, snapshot.OrderItems);
        InsertPackagingItems(connection, transaction, snapshot.PackagingItems);
        InsertOrderPackaging(connection, transaction, snapshot.OrderPackaging);

        transaction.Commit();
        return snapshot.TotalRows;
    }

    private static void DeleteAll(SqliteConnection connection, SqliteTransaction transaction, string table)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"DELETE FROM {table};";
        command.ExecuteNonQuery();
    }

    private static void InsertIngredientCategories(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<SyncIngredientCategory> items)
    {
        foreach (var item in items)
        {
            using var command = CreateCommand(
                connection,
                transaction,
                """
                INSERT INTO ingredient_categories (id, profile_id, name, created_at)
                VALUES ($id, $profile, $name, $created);
                """);
            command.Parameters.AddWithValue("$id", item.Id.ToString());
            command.Parameters.AddWithValue("$profile", ProfileId);
            command.Parameters.AddWithValue("$name", item.Name);
            command.Parameters.AddWithValue("$created", Timestamp(item.CreatedAt));
            command.ExecuteNonQuery();
        }
    }

    private static void InsertRecipeCategories(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<SyncRecipeCategory> items)
    {
        foreach (var item in items)
        {
            using var command = CreateCommand(
                connection,
                transaction,
                """
                INSERT INTO recipe_categories (id, profile_id, name, created_at)
                VALUES ($id, $profile, $name, $created);
                """);
            command.Parameters.AddWithValue("$id", item.Id.ToString());
            command.Parameters.AddWithValue("$profile", ProfileId);
            command.Parameters.AddWithValue("$name", item.Name);
            command.Parameters.AddWithValue("$created", Timestamp(item.CreatedAt));
            command.ExecuteNonQuery();
        }
    }

    private static void InsertPackagingCategories(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<SyncPackagingCategory> items)
    {
        foreach (var item in items)
        {
            using var command = CreateCommand(
                connection,
                transaction,
                """
                INSERT INTO packaging_categories (id, profile_id, name, created_at)
                VALUES ($id, $profile, $name, $created);
                """);
            command.Parameters.AddWithValue("$id", item.Id.ToString());
            command.Parameters.AddWithValue("$profile", ProfileId);
            command.Parameters.AddWithValue("$name", item.Name);
            command.Parameters.AddWithValue("$created", Timestamp(item.CreatedAt));
            command.ExecuteNonQuery();
        }
    }

    private static void InsertIngredients(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<SyncIngredient> items)
    {
        foreach (var item in items)
        {
            using var command = CreateCommand(
                connection,
                transaction,
                """
                INSERT INTO ingredients
                    (id, profile_id, name, brand, unit, category_id, created_at, updated_at, deleted_at)
                VALUES
                    ($id, $profile, $name, $brand, $unit, $category, $created, $updated, $deleted);
                """);
            command.Parameters.AddWithValue("$id", item.Id.ToString());
            command.Parameters.AddWithValue("$profile", ProfileId);
            command.Parameters.AddWithValue("$name", item.Name);
            command.Parameters.AddWithValue("$brand", (object?)item.Brand ?? DBNull.Value);
            command.Parameters.AddWithValue("$unit", item.Unit);
            command.Parameters.AddWithValue("$category", (object?)item.CategoryId?.ToString() ?? DBNull.Value);
            command.Parameters.AddWithValue("$created", Timestamp(item.CreatedAt));
            command.Parameters.AddWithValue("$updated", Timestamp(item.UpdatedAt));
            command.Parameters.AddWithValue("$deleted", (object?)NullableTimestamp(item.DeletedAt) ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
    }

    private static void InsertPurchases(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<SyncPurchase> items)
    {
        foreach (var item in items)
        {
            using var command = CreateCommand(
                connection,
                transaction,
                """
                INSERT INTO purchases
                    (id, profile_id, ingredient_id, purchased_on, store, quantity_milli,
                     total_price_kopecks, order_id, note, created_at)
                VALUES
                    ($id, $profile, $ingredient, $date, $store, $quantity,
                     $total, $order, $note, $created);
                """);
            command.Parameters.AddWithValue("$id", item.Id.ToString());
            command.Parameters.AddWithValue("$profile", ProfileId);
            command.Parameters.AddWithValue("$ingredient", item.IngredientId.ToString());
            command.Parameters.AddWithValue("$date", Date(item.PurchasedOn));
            command.Parameters.AddWithValue("$store", (object?)item.Store ?? DBNull.Value);
            command.Parameters.AddWithValue("$quantity", item.QuantityMilli);
            command.Parameters.AddWithValue("$total", item.TotalPriceKopecks);
            command.Parameters.AddWithValue("$order", (object?)item.OrderId?.ToString() ?? DBNull.Value);
            command.Parameters.AddWithValue("$note", (object?)item.Note ?? DBNull.Value);
            command.Parameters.AddWithValue("$created", Timestamp(item.CreatedAt));
            command.ExecuteNonQuery();
        }
    }

    private static void InsertRecipes(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<SyncRecipe> items)
    {
        foreach (var item in items)
        {
            using var command = CreateCommand(
                connection,
                transaction,
                """
                INSERT INTO recipes
                    (id, profile_id, name, category_id, output_quantity_milli, output_unit,
                     markup_percent, technology, photo, created_at, updated_at, deleted_at)
                VALUES
                    ($id, $profile, $name, $category, $output, $unit,
                     $markup, $technology, $photo, $created, $updated, $deleted);
                """);
            command.Parameters.AddWithValue("$id", item.Id.ToString());
            command.Parameters.AddWithValue("$profile", ProfileId);
            command.Parameters.AddWithValue("$name", item.Name);
            command.Parameters.AddWithValue("$category", (object?)item.CategoryId?.ToString() ?? DBNull.Value);
            command.Parameters.AddWithValue("$output", item.OutputQuantityMilli);
            command.Parameters.AddWithValue("$unit", item.OutputUnit);
            command.Parameters.AddWithValue("$markup", item.MarkupPercent);
            command.Parameters.AddWithValue("$technology", (object?)item.Technology ?? DBNull.Value);
            command.Parameters.AddWithValue("$photo", (object?)Photo(item.PhotoBase64) ?? DBNull.Value);
            command.Parameters.AddWithValue("$created", Timestamp(item.CreatedAt));
            command.Parameters.AddWithValue("$updated", Timestamp(item.UpdatedAt));
            command.Parameters.AddWithValue("$deleted", (object?)NullableTimestamp(item.DeletedAt) ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
    }

    private static void InsertRecipeIngredients(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<SyncRecipeIngredient> items)
    {
        foreach (var item in items)
        {
            using var command = CreateCommand(
                connection,
                transaction,
                """
                INSERT INTO recipe_ingredients (recipe_id, ingredient_id, amount_milli, position)
                VALUES ($recipe, $ingredient, $amount, $position);
                """);
            command.Parameters.AddWithValue("$recipe", item.RecipeId.ToString());
            command.Parameters.AddWithValue("$ingredient", item.IngredientId.ToString());
            command.Parameters.AddWithValue("$amount", item.AmountMilli);
            command.Parameters.AddWithValue("$position", item.Position);
            command.ExecuteNonQuery();
        }
    }

    private static void InsertCustomers(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<SyncCustomer> items)
    {
        foreach (var item in items)
        {
            using var command = CreateCommand(
                connection,
                transaction,
                """
                INSERT INTO customers
                    (id, profile_id, number, first_name, last_name, phone, phone_normalized,
                     created_at, updated_at)
                VALUES
                    ($id, $profile, $number, $first, $last, $phone, $normalized, $created, $updated);
                """);
            command.Parameters.AddWithValue("$id", item.Id.ToString());
            command.Parameters.AddWithValue("$profile", ProfileId);
            command.Parameters.AddWithValue("$number", item.Number);
            command.Parameters.AddWithValue("$first", item.FirstName);
            command.Parameters.AddWithValue("$last", (object?)item.LastName ?? DBNull.Value);
            command.Parameters.AddWithValue("$phone", item.Phone);
            command.Parameters.AddWithValue("$normalized", item.PhoneNormalized);
            command.Parameters.AddWithValue("$created", Timestamp(item.CreatedAt));
            command.Parameters.AddWithValue("$updated", Timestamp(item.UpdatedAt));
            command.ExecuteNonQuery();
        }
    }

    private static void InsertOrders(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<SyncOrder> items)
    {
        foreach (var item in items)
        {
            using var command = CreateCommand(
                connection,
                transaction,
                """
                INSERT INTO orders
                    (id, profile_id, number, first_name, last_name, phone, due_date, status,
                     comment, total_price_kopecks, paid_amount_kopecks, discount_kopecks,
                     customer_id, created_at, updated_at)
                VALUES
                    ($id, $profile, $number, $first, $last, $phone, $due, $status,
                     $comment, $total, $paid, $discount, $customer, $created, $updated);
                """);
            command.Parameters.AddWithValue("$id", item.Id.ToString());
            command.Parameters.AddWithValue("$profile", ProfileId);
            command.Parameters.AddWithValue("$number", item.Number);
            command.Parameters.AddWithValue("$first", item.FirstName);
            command.Parameters.AddWithValue("$last", (object?)item.LastName ?? DBNull.Value);
            command.Parameters.AddWithValue("$phone", item.Phone);
            command.Parameters.AddWithValue("$due", Date(item.DueDate));
            command.Parameters.AddWithValue("$status", item.Status);
            command.Parameters.AddWithValue("$comment", (object?)item.Comment ?? DBNull.Value);
            command.Parameters.AddWithValue("$total", item.TotalPriceKopecks);
            command.Parameters.AddWithValue("$paid", item.PaidAmountKopecks);
            command.Parameters.AddWithValue("$discount", item.DiscountKopecks);
            command.Parameters.AddWithValue("$customer", (object?)item.CustomerId?.ToString() ?? DBNull.Value);
            command.Parameters.AddWithValue("$created", Timestamp(item.CreatedAt));
            command.Parameters.AddWithValue("$updated", Timestamp(item.UpdatedAt));
            command.ExecuteNonQuery();
        }
    }

    private static void InsertOrderItems(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<SyncOrderItem> items)
    {
        foreach (var item in items)
        {
            using var command = CreateCommand(
                connection,
                transaction,
                """
                INSERT INTO order_items
                    (id, order_id, recipe_id, recipe_name, quantity_milli, unit_price_kopecks,
                     cost_price_kopecks, cost_estimated, output_quantity_milli, output_unit)
                VALUES
                    ($id, $order, $recipe, $name, $quantity, $unit_price,
                     $cost, $estimated, $output, $output_unit);
                """);
            command.Parameters.AddWithValue("$id", item.Id.ToString());
            command.Parameters.AddWithValue("$order", item.OrderId.ToString());
            command.Parameters.AddWithValue("$recipe", (object?)item.RecipeId?.ToString() ?? DBNull.Value);
            command.Parameters.AddWithValue("$name", item.RecipeName);
            command.Parameters.AddWithValue("$quantity", item.QuantityMilli);
            command.Parameters.AddWithValue("$unit_price", item.UnitPriceKopecks);
            command.Parameters.AddWithValue("$cost", item.CostPriceKopecks);
            command.Parameters.AddWithValue("$estimated", item.CostEstimated ? 1 : 0);
            command.Parameters.AddWithValue("$output", item.OutputQuantityMilli);
            command.Parameters.AddWithValue("$output_unit", item.OutputUnit);
            command.ExecuteNonQuery();
        }
    }

    private static void InsertPackagingItems(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<SyncPackagingItem> items)
    {
        foreach (var item in items)
        {
            using var command = CreateCommand(
                connection,
                transaction,
                """
                INSERT INTO packaging_items
                    (id, profile_id, category_id, name, size, price_kopecks, created_at, updated_at)
                VALUES
                    ($id, $profile, $category, $name, $size, $price, $created, $updated);
                """);
            command.Parameters.AddWithValue("$id", item.Id.ToString());
            command.Parameters.AddWithValue("$profile", ProfileId);
            command.Parameters.AddWithValue("$category", (object?)item.CategoryId?.ToString() ?? DBNull.Value);
            command.Parameters.AddWithValue("$name", item.Name);
            command.Parameters.AddWithValue("$size", (object?)item.Size ?? DBNull.Value);
            command.Parameters.AddWithValue("$price", item.PriceKopecks);
            command.Parameters.AddWithValue("$created", Timestamp(item.CreatedAt));
            command.Parameters.AddWithValue("$updated", Timestamp(item.UpdatedAt));
            command.ExecuteNonQuery();
        }
    }

    private static void InsertOrderPackaging(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<SyncOrderPackaging> items)
    {
        foreach (var item in items)
        {
            using var command = CreateCommand(
                connection,
                transaction,
                """
                INSERT INTO order_packaging
                    (id, order_id, packaging_id, name, size, cost_kopecks,
                     sale_price_kopecks, quantity, position)
                VALUES
                    ($id, $order, $packaging, $name, $size, $cost,
                     $sale, $quantity, $position);
                """);
            command.Parameters.AddWithValue("$id", item.Id.ToString());
            command.Parameters.AddWithValue("$order", item.OrderId.ToString());
            command.Parameters.AddWithValue("$packaging", (object?)item.PackagingId?.ToString() ?? DBNull.Value);
            command.Parameters.AddWithValue("$name", item.Name);
            command.Parameters.AddWithValue("$size", (object?)item.Size ?? DBNull.Value);
            command.Parameters.AddWithValue("$cost", item.CostKopecks);
            command.Parameters.AddWithValue("$sale", item.SalePriceKopecks);
            command.Parameters.AddWithValue("$quantity", item.Quantity);
            command.Parameters.AddWithValue("$position", item.Position);
            command.ExecuteNonQuery();
        }
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

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

    private static string? NullableTimestamp(DateTimeOffset? value) =>
        value is { } timestamp ? Timestamp(timestamp) : null;

    private static string Date(DateOnly value) =>
        value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static byte[]? Photo(string? base64) =>
        base64 is null ? null : Convert.FromBase64String(base64);
}
