using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SweetTaste.Core.Sync;

/// <summary>
/// Переводит снапшот в строки облачных таблиц: имена колонок, типы и форматы
/// значений PostgREST.
/// </summary>
/// <remarks>
/// Отличия от SQLite, которые здесь и закрываются:
/// <list type="bullet">
/// <item>GUID — строки <c>uuid</c>, а не TEXT;</item>
/// <item><c>cost_estimated</c> — <c>boolean</c>, а не 0/1;</item>
/// <item>фото — <c>bytea</c>, PostgREST принимает его как <c>\x...</c> в hex;</item>
/// <item>даты — <c>yyyy-MM-dd</c>, метки времени — ISO 8601 с зоной.</item>
/// </list>
/// <c>user_id</c> в строках не передаётся: колонка заполняется из <c>auth.uid()</c>
/// на сервере, а RLS не даст записать чужое значение.
/// </remarks>
public static class SnapshotRows
{
    /// <summary>Порядок загрузки: сначала справочники, потом то, что на них ссылается.</summary>
    public static IReadOnlyList<string> LoadOrder { get; } = new[]
    {
        SyncTables.IngredientCategories,
        SyncTables.RecipeCategories,
        SyncTables.PackagingCategories,
        SyncTables.Ingredients,
        SyncTables.Purchases,
        SyncTables.Recipes,
        SyncTables.RecipeIngredients,
        SyncTables.Customers,
        SyncTables.Orders,
        SyncTables.OrderItems,
        SyncTables.PackagingItems,
        SyncTables.OrderPackaging,
    };

    /// <summary>Строки одной таблицы в том виде, в каком их ждёт PostgREST.</summary>
    public static IReadOnlyList<Dictionary<string, object?>> For(SyncSnapshot snapshot, string table)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return table switch
        {
            SyncTables.IngredientCategories => snapshot.IngredientCategories.Select(ToRow).ToList(),
            SyncTables.RecipeCategories => snapshot.RecipeCategories.Select(ToRow).ToList(),
            SyncTables.PackagingCategories => snapshot.PackagingCategories.Select(ToRow).ToList(),
            SyncTables.Ingredients => snapshot.Ingredients.Select(ToRow).ToList(),
            SyncTables.Purchases => snapshot.Purchases.Select(ToRow).ToList(),
            SyncTables.Recipes => snapshot.Recipes.Select(ToRow).ToList(),
            SyncTables.RecipeIngredients => snapshot.RecipeIngredients.Select(ToRow).ToList(),
            SyncTables.Customers => snapshot.Customers.Select(ToRow).ToList(),
            SyncTables.Orders => snapshot.Orders.Select(ToRow).ToList(),
            SyncTables.OrderItems => snapshot.OrderItems.Select(ToRow).ToList(),
            SyncTables.PackagingItems => snapshot.PackagingItems.Select(ToRow).ToList(),
            SyncTables.OrderPackaging => snapshot.OrderPackaging.Select(ToRow).ToList(),
            _ => throw new ArgumentOutOfRangeException(nameof(table), table, "Неизвестная таблица снапшота."),
        };
    }

    /// <summary>Тело запроса: массив строк в JSON.</summary>
    public static string ToJson(IReadOnlyList<Dictionary<string, object?>> rows) =>
        JsonSerializer.Serialize(rows, JsonOptions);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static Dictionary<string, object?> ToRow(SyncIngredientCategory item) => new()
    {
        ["id"] = item.Id.ToString(),
        ["name"] = item.Name,
        ["created_at"] = Timestamp(item.CreatedAt),
    };

    private static Dictionary<string, object?> ToRow(SyncRecipeCategory item) => new()
    {
        ["id"] = item.Id.ToString(),
        ["name"] = item.Name,
        ["created_at"] = Timestamp(item.CreatedAt),
    };

    private static Dictionary<string, object?> ToRow(SyncPackagingCategory item) => new()
    {
        ["id"] = item.Id.ToString(),
        ["name"] = item.Name,
        ["created_at"] = Timestamp(item.CreatedAt),
    };

    private static Dictionary<string, object?> ToRow(SyncIngredient item) => new()
    {
        ["id"] = item.Id.ToString(),
        ["name"] = item.Name,
        ["brand"] = item.Brand,
        ["unit"] = item.Unit,
        ["category_id"] = item.CategoryId?.ToString(),
        ["created_at"] = Timestamp(item.CreatedAt),
        ["updated_at"] = Timestamp(item.UpdatedAt),
        ["deleted_at"] = item.DeletedAt is { } deleted ? Timestamp(deleted) : null,
    };

    private static Dictionary<string, object?> ToRow(SyncPurchase item) => new()
    {
        ["id"] = item.Id.ToString(),
        ["ingredient_id"] = item.IngredientId.ToString(),
        ["purchased_on"] = Date(item.PurchasedOn),
        ["store"] = item.Store,
        ["quantity_milli"] = item.QuantityMilli,
        ["total_price_kopecks"] = item.TotalPriceKopecks,
        ["order_id"] = item.OrderId?.ToString(),
        ["note"] = item.Note,
        ["created_at"] = Timestamp(item.CreatedAt),
    };

    private static Dictionary<string, object?> ToRow(SyncRecipe item) => new()
    {
        ["id"] = item.Id.ToString(),
        ["name"] = item.Name,
        ["category_id"] = item.CategoryId?.ToString(),
        ["output_quantity_milli"] = item.OutputQuantityMilli,
        ["output_unit"] = item.OutputUnit,
        ["markup_percent"] = item.MarkupPercent,
        ["technology"] = item.Technology,
        ["photo"] = item.PhotoBase64 is null ? null : Bytea(item.PhotoBase64),
        ["created_at"] = Timestamp(item.CreatedAt),
        ["updated_at"] = Timestamp(item.UpdatedAt),
        ["deleted_at"] = item.DeletedAt is { } deleted ? Timestamp(deleted) : null,
    };

    private static Dictionary<string, object?> ToRow(SyncRecipeIngredient item) => new()
    {
        ["recipe_id"] = item.RecipeId.ToString(),
        ["ingredient_id"] = item.IngredientId.ToString(),
        ["amount_milli"] = item.AmountMilli,
        ["position"] = item.Position,
    };

    private static Dictionary<string, object?> ToRow(SyncCustomer item) => new()
    {
        ["id"] = item.Id.ToString(),
        ["number"] = item.Number,
        ["first_name"] = item.FirstName,
        ["last_name"] = item.LastName,
        ["phone"] = item.Phone,
        ["phone_normalized"] = item.PhoneNormalized,
        ["created_at"] = Timestamp(item.CreatedAt),
        ["updated_at"] = Timestamp(item.UpdatedAt),
    };

    private static Dictionary<string, object?> ToRow(SyncOrder item) => new()
    {
        ["id"] = item.Id.ToString(),
        ["number"] = item.Number,
        ["first_name"] = item.FirstName,
        ["last_name"] = item.LastName,
        ["phone"] = item.Phone,
        ["due_date"] = Date(item.DueDate),
        ["status"] = item.Status,
        ["comment"] = item.Comment,
        ["total_price_kopecks"] = item.TotalPriceKopecks,
        ["paid_amount_kopecks"] = item.PaidAmountKopecks,
        ["discount_kopecks"] = item.DiscountKopecks,
        ["customer_id"] = item.CustomerId?.ToString(),
        ["created_at"] = Timestamp(item.CreatedAt),
        ["updated_at"] = Timestamp(item.UpdatedAt),
    };

    private static Dictionary<string, object?> ToRow(SyncOrderItem item) => new()
    {
        ["id"] = item.Id.ToString(),
        ["order_id"] = item.OrderId.ToString(),
        ["recipe_id"] = item.RecipeId?.ToString(),
        ["recipe_name"] = item.RecipeName,
        ["quantity_milli"] = item.QuantityMilli,
        ["unit_price_kopecks"] = item.UnitPriceKopecks,
        ["cost_price_kopecks"] = item.CostPriceKopecks,
        ["cost_estimated"] = item.CostEstimated,
        ["output_quantity_milli"] = item.OutputQuantityMilli,
        ["output_unit"] = item.OutputUnit,
    };

    private static Dictionary<string, object?> ToRow(SyncPackagingItem item) => new()
    {
        ["id"] = item.Id.ToString(),
        ["category_id"] = item.CategoryId?.ToString(),
        ["name"] = item.Name,
        ["size"] = item.Size,
        ["price_kopecks"] = item.PriceKopecks,
        ["created_at"] = Timestamp(item.CreatedAt),
        ["updated_at"] = Timestamp(item.UpdatedAt),
    };

    private static Dictionary<string, object?> ToRow(SyncOrderPackaging item) => new()
    {
        ["id"] = item.Id.ToString(),
        ["order_id"] = item.OrderId.ToString(),
        ["packaging_id"] = item.PackagingId?.ToString(),
        ["name"] = item.Name,
        ["size"] = item.Size,
        ["cost_kopecks"] = item.CostKopecks,
        ["sale_price_kopecks"] = item.SalePriceKopecks,
        ["quantity"] = item.Quantity,
        ["position"] = item.Position,
    };

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

    private static string Date(DateOnly value) =>
        value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>PostgREST принимает bytea в hex-виде с префиксом <c>\x</c>.</summary>
    private static string Bytea(string base64) =>
        "\\x" + Convert.ToHexString(Convert.FromBase64String(base64)).ToLowerInvariant();
}
