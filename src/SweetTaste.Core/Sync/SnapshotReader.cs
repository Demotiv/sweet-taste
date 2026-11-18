using System.Globalization;
using System.Text.Json;

namespace SweetTaste.Core.Sync;

/// <summary>
/// Разбирает ответы PostgREST в <see cref="SyncSnapshot"/>: обратное преобразование
/// к <see cref="SnapshotRows"/>.
/// </summary>
/// <remarks>
/// Отличия облака от SQLite, которые здесь закрываются:
/// <list type="bullet">
/// <item>GUID приходят строками <c>uuid</c>;</item>
/// <item><c>cost_estimated</c> — настоящий <c>boolean</c>, а не 0/1;</item>
/// <item>фото приходит как <c>bytea</c> в hex с префиксом <c>\x</c>;</item>
/// <item>даты — <c>yyyy-MM-dd</c>, метки времени — ISO 8601 с зоной.</item>
/// </list>
/// <c>user_id</c> из ответа игнорируется: он всегда равен текущему пользователю.
/// </remarks>
public static class SnapshotReader
{
    /// <summary>Разбирает тело ответа одной таблицы в строки-словари.</summary>
    public static IReadOnlyList<Dictionary<string, JsonElement>> ParseRows(string body, string table)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return Array.Empty<Dictionary<string, JsonElement>>();
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new SyncException($"Сервер вернул не список строк ({table}).", table);
            }

            var rows = new List<Dictionary<string, JsonElement>>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                var row = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    // Клонируем: JsonDocument освобождается вместе с методом.
                    row[property.Name] = property.Value.Clone();
                }

                rows.Add(row);
            }

            return rows;
        }
        catch (JsonException exception)
        {
            throw new SyncException($"Не удалось разобрать ответ сервера ({table}).", table, exception);
        }
    }

    /// <summary>Собирает снапшот из строк всех таблиц.</summary>
    public static SyncSnapshot Read(
        IReadOnlyDictionary<string, IReadOnlyList<Dictionary<string, JsonElement>>> tables,
        DateTimeOffset takenAt)
    {
        ArgumentNullException.ThrowIfNull(tables);

        return new SyncSnapshot
        {
            SchemaVersion = DatabaseSchemaVersion,
            TakenAt = takenAt,
            IngredientCategories = Rows(tables, SyncTables.IngredientCategories).Select(ReadIngredientCategory).ToList(),
            RecipeCategories = Rows(tables, SyncTables.RecipeCategories).Select(ReadRecipeCategory).ToList(),
            PackagingCategories = Rows(tables, SyncTables.PackagingCategories).Select(ReadPackagingCategory).ToList(),
            Ingredients = Rows(tables, SyncTables.Ingredients).Select(ReadIngredient).ToList(),
            Purchases = Rows(tables, SyncTables.Purchases).Select(ReadPurchase).ToList(),
            Recipes = Rows(tables, SyncTables.Recipes).Select(ReadRecipe).ToList(),
            RecipeIngredients = Rows(tables, SyncTables.RecipeIngredients).Select(ReadRecipeIngredient).ToList(),
            Customers = Rows(tables, SyncTables.Customers).Select(ReadCustomer).ToList(),
            Orders = Rows(tables, SyncTables.Orders).Select(ReadOrder).ToList(),
            OrderItems = Rows(tables, SyncTables.OrderItems).Select(ReadOrderItem).ToList(),
            PackagingItems = Rows(tables, SyncTables.PackagingItems).Select(ReadPackagingItem).ToList(),
            OrderPackaging = Rows(tables, SyncTables.OrderPackaging).Select(ReadOrderPackaging).ToList(),
        };
    }

    /// <summary>Версия схемы, которую понимает приложение.</summary>
    public const int DatabaseSchemaVersion = 15;

    private static IReadOnlyList<Dictionary<string, JsonElement>> Rows(
        IReadOnlyDictionary<string, IReadOnlyList<Dictionary<string, JsonElement>>> tables,
        string table) =>
        tables.TryGetValue(table, out var rows) ? rows : Array.Empty<Dictionary<string, JsonElement>>();

    private static SyncIngredientCategory ReadIngredientCategory(Dictionary<string, JsonElement> row) => new()
    {
        Id = Guid(row, "id"),
        Name = Text(row, "name"),
        CreatedAt = Timestamp(row, "created_at"),
    };

    private static SyncRecipeCategory ReadRecipeCategory(Dictionary<string, JsonElement> row) => new()
    {
        Id = Guid(row, "id"),
        Name = Text(row, "name"),
        CreatedAt = Timestamp(row, "created_at"),
    };

    private static SyncPackagingCategory ReadPackagingCategory(Dictionary<string, JsonElement> row) => new()
    {
        Id = Guid(row, "id"),
        Name = Text(row, "name"),
        CreatedAt = Timestamp(row, "created_at"),
    };

    private static SyncIngredient ReadIngredient(Dictionary<string, JsonElement> row) => new()
    {
        Id = Guid(row, "id"),
        Name = Text(row, "name"),
        Brand = OptionalText(row, "brand"),
        Unit = Text(row, "unit"),
        CategoryId = OptionalGuid(row, "category_id"),
        CreatedAt = Timestamp(row, "created_at"),
        UpdatedAt = Timestamp(row, "updated_at"),
        DeletedAt = OptionalTimestamp(row, "deleted_at"),
    };

    private static SyncPurchase ReadPurchase(Dictionary<string, JsonElement> row) => new()
    {
        Id = Guid(row, "id"),
        IngredientId = Guid(row, "ingredient_id"),
        PurchasedOn = Date(row, "purchased_on"),
        Store = OptionalText(row, "store"),
        QuantityMilli = Integer(row, "quantity_milli"),
        TotalPriceKopecks = Integer(row, "total_price_kopecks"),
        OrderId = OptionalGuid(row, "order_id"),
        Note = OptionalText(row, "note"),
        CreatedAt = Timestamp(row, "created_at"),
    };

    private static SyncRecipe ReadRecipe(Dictionary<string, JsonElement> row) => new()
    {
        Id = Guid(row, "id"),
        Name = Text(row, "name"),
        CategoryId = OptionalGuid(row, "category_id"),
        OutputQuantityMilli = Integer(row, "output_quantity_milli"),
        OutputUnit = Text(row, "output_unit"),
        MarkupPercent = (int)Integer(row, "markup_percent"),
        Technology = OptionalText(row, "technology"),
        PhotoBase64 = OptionalBytea(row, "photo"),
        CreatedAt = Timestamp(row, "created_at"),
        UpdatedAt = Timestamp(row, "updated_at"),
        DeletedAt = OptionalTimestamp(row, "deleted_at"),
    };

    private static SyncRecipeIngredient ReadRecipeIngredient(Dictionary<string, JsonElement> row) => new()
    {
        RecipeId = Guid(row, "recipe_id"),
        IngredientId = Guid(row, "ingredient_id"),
        AmountMilli = Integer(row, "amount_milli"),
        Position = (int)Integer(row, "position"),
    };

    private static SyncCustomer ReadCustomer(Dictionary<string, JsonElement> row) => new()
    {
        Id = Guid(row, "id"),
        Number = (int)Integer(row, "number"),
        FirstName = Text(row, "first_name"),
        LastName = OptionalText(row, "last_name"),
        Phone = Text(row, "phone"),
        PhoneNormalized = Text(row, "phone_normalized"),
        CreatedAt = Timestamp(row, "created_at"),
        UpdatedAt = Timestamp(row, "updated_at"),
    };

    private static SyncOrder ReadOrder(Dictionary<string, JsonElement> row) => new()
    {
        Id = Guid(row, "id"),
        Number = (int)Integer(row, "number"),
        FirstName = Text(row, "first_name"),
        LastName = OptionalText(row, "last_name"),
        Phone = Text(row, "phone"),
        DueDate = Date(row, "due_date"),
        Status = Text(row, "status"),
        Comment = OptionalText(row, "comment"),
        TotalPriceKopecks = Integer(row, "total_price_kopecks"),
        PaidAmountKopecks = Integer(row, "paid_amount_kopecks"),
        DiscountKopecks = Integer(row, "discount_kopecks"),
        CustomerId = OptionalGuid(row, "customer_id"),
        CreatedAt = Timestamp(row, "created_at"),
        UpdatedAt = Timestamp(row, "updated_at"),
    };

    private static SyncOrderItem ReadOrderItem(Dictionary<string, JsonElement> row) => new()
    {
        Id = Guid(row, "id"),
        OrderId = Guid(row, "order_id"),
        RecipeId = OptionalGuid(row, "recipe_id"),
        RecipeName = Text(row, "recipe_name"),
        QuantityMilli = Integer(row, "quantity_milli"),
        UnitPriceKopecks = Integer(row, "unit_price_kopecks"),
        CostPriceKopecks = Integer(row, "cost_price_kopecks"),
        CostEstimated = Boolean(row, "cost_estimated"),
        OutputQuantityMilli = Integer(row, "output_quantity_milli"),
        OutputUnit = Text(row, "output_unit"),
    };

    private static SyncPackagingItem ReadPackagingItem(Dictionary<string, JsonElement> row) => new()
    {
        Id = Guid(row, "id"),
        CategoryId = OptionalGuid(row, "category_id"),
        Name = Text(row, "name"),
        Size = OptionalText(row, "size"),
        PriceKopecks = Integer(row, "price_kopecks"),
        CreatedAt = Timestamp(row, "created_at"),
        UpdatedAt = Timestamp(row, "updated_at"),
    };

    private static SyncOrderPackaging ReadOrderPackaging(Dictionary<string, JsonElement> row) => new()
    {
        Id = Guid(row, "id"),
        OrderId = Guid(row, "order_id"),
        PackagingId = OptionalGuid(row, "packaging_id"),
        Name = Text(row, "name"),
        Size = OptionalText(row, "size"),
        CostKopecks = Integer(row, "cost_kopecks"),
        SalePriceKopecks = Integer(row, "sale_price_kopecks"),
        Quantity = (int)Integer(row, "quantity"),
        Position = (int)Integer(row, "position"),
    };

    private static JsonElement Value(Dictionary<string, JsonElement> row, string column) =>
        row.TryGetValue(column, out var value)
            ? value
            : throw new SyncException($"В ответе сервера нет колонки «{column}».");

    private static bool IsNull(JsonElement value) =>
        value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;

    private static Guid Guid(Dictionary<string, JsonElement> row, string column) =>
        System.Guid.Parse(Text(row, column));

    private static Guid? OptionalGuid(Dictionary<string, JsonElement> row, string column)
    {
        var value = Value(row, column);
        return IsNull(value) ? null : System.Guid.Parse(value.GetString()!);
    }

    private static string Text(Dictionary<string, JsonElement> row, string column) =>
        Value(row, column).GetString()
        ?? throw new SyncException($"Пустое значение в колонке «{column}».");

    private static string? OptionalText(Dictionary<string, JsonElement> row, string column)
    {
        var value = Value(row, column);
        return IsNull(value) ? null : value.GetString();
    }

    private static long Integer(Dictionary<string, JsonElement> row, string column) =>
        Value(row, column).GetInt64();

    private static bool Boolean(Dictionary<string, JsonElement> row, string column) =>
        Value(row, column).GetBoolean();

    private static DateTimeOffset Timestamp(Dictionary<string, JsonElement> row, string column) =>
        DateTimeOffset.Parse(Text(row, column), CultureInfo.InvariantCulture);

    private static DateTimeOffset? OptionalTimestamp(Dictionary<string, JsonElement> row, string column)
    {
        var value = Value(row, column);
        return IsNull(value)
            ? null
            : DateTimeOffset.Parse(value.GetString()!, CultureInfo.InvariantCulture);
    }

    private static DateOnly Date(Dictionary<string, JsonElement> row, string column) =>
        DateOnly.ParseExact(Text(row, column), "yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>PostgREST отдаёт <c>bytea</c> как <c>\x...</c> в hex.</summary>
    private static string? OptionalBytea(Dictionary<string, JsonElement> row, string column)
    {
        var value = Value(row, column);
        if (IsNull(value))
        {
            return null;
        }

        var text = value.GetString() ?? string.Empty;
        if (text.StartsWith("\\x", StringComparison.Ordinal))
        {
            text = text[2..];
        }

        return text.Length == 0 ? null : Convert.ToBase64String(Convert.FromHexString(text));
    }
}
