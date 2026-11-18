using System.Text.Json.Serialization;

namespace SweetTaste.Core.Sync;

/// <summary>
/// Полная копия бизнес-данных для облака. Локальная база — источник истины,
/// снапшот — её снимок на момент выгрузки.
/// </summary>
/// <remarks>
/// Настройки устройства (<c>app_settings</c>) и профиль кондитера
/// (<c>confectioner_profile</c>) в снапшот не входят: это локальные данные.
/// </remarks>
public sealed record SyncSnapshot
{
    public required int SchemaVersion { get; init; }

    public required DateTimeOffset TakenAt { get; init; }

    public required IReadOnlyList<SyncIngredientCategory> IngredientCategories { get; init; }

    public required IReadOnlyList<SyncRecipeCategory> RecipeCategories { get; init; }

    public required IReadOnlyList<SyncPackagingCategory> PackagingCategories { get; init; }

    public required IReadOnlyList<SyncIngredient> Ingredients { get; init; }

    public required IReadOnlyList<SyncPurchase> Purchases { get; init; }

    public required IReadOnlyList<SyncRecipe> Recipes { get; init; }

    public required IReadOnlyList<SyncRecipeIngredient> RecipeIngredients { get; init; }

    public required IReadOnlyList<SyncCustomer> Customers { get; init; }

    public required IReadOnlyList<SyncOrder> Orders { get; init; }

    public required IReadOnlyList<SyncOrderItem> OrderItems { get; init; }

    public required IReadOnlyList<SyncPackagingItem> PackagingItems { get; init; }

    public required IReadOnlyList<SyncOrderPackaging> OrderPackaging { get; init; }

    /// <summary>Число строк по каждой таблице — для сверки после загрузки.</summary>
    public IReadOnlyDictionary<string, int> RowCounts => new Dictionary<string, int>
    {
        [SyncTables.IngredientCategories] = IngredientCategories.Count,
        [SyncTables.RecipeCategories] = RecipeCategories.Count,
        [SyncTables.PackagingCategories] = PackagingCategories.Count,
        [SyncTables.Ingredients] = Ingredients.Count,
        [SyncTables.Purchases] = Purchases.Count,
        [SyncTables.Recipes] = Recipes.Count,
        [SyncTables.RecipeIngredients] = RecipeIngredients.Count,
        [SyncTables.Customers] = Customers.Count,
        [SyncTables.Orders] = Orders.Count,
        [SyncTables.OrderItems] = OrderItems.Count,
        [SyncTables.PackagingItems] = PackagingItems.Count,
        [SyncTables.OrderPackaging] = OrderPackaging.Count,
    };

    public int TotalRows => RowCounts.Values.Sum();
}

/// <summary>Имена таблиц облака. Совпадают с именами таблиц SQLite.</summary>
public static class SyncTables
{
    public const string IngredientCategories = "ingredient_categories";
    public const string RecipeCategories = "recipe_categories";
    public const string PackagingCategories = "packaging_categories";
    public const string Ingredients = "ingredients";
    public const string Purchases = "purchases";
    public const string Recipes = "recipes";
    public const string RecipeIngredients = "recipe_ingredients";
    public const string Customers = "customers";
    public const string Orders = "orders";
    public const string OrderItems = "order_items";
    public const string PackagingItems = "packaging_items";
    public const string OrderPackaging = "order_packaging";
}

public sealed record SyncIngredientCategory
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record SyncRecipeCategory
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record SyncPackagingCategory
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record SyncIngredient
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public string? Brand { get; init; }

    public required string Unit { get; init; }

    public Guid? CategoryId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? DeletedAt { get; init; }
}

public sealed record SyncPurchase
{
    public required Guid Id { get; init; }

    public required Guid IngredientId { get; init; }

    public required DateOnly PurchasedOn { get; init; }

    public string? Store { get; init; }

    public required long QuantityMilli { get; init; }

    public required long TotalPriceKopecks { get; init; }

    public Guid? OrderId { get; init; }

    public string? Note { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record SyncRecipe
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public Guid? CategoryId { get; init; }

    public required long OutputQuantityMilli { get; init; }

    public required string OutputUnit { get; init; }

    public required int MarkupPercent { get; init; }

    public string? Technology { get; init; }

    /// <summary>Фото в base64: JSON не умеет байты, а PostgREST ждёт строку для bytea.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PhotoBase64 { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? DeletedAt { get; init; }
}

public sealed record SyncRecipeIngredient
{
    public required Guid RecipeId { get; init; }

    public required Guid IngredientId { get; init; }

    public required long AmountMilli { get; init; }

    public required int Position { get; init; }
}

public sealed record SyncCustomer
{
    public required Guid Id { get; init; }

    public required int Number { get; init; }

    public required string FirstName { get; init; }

    public string? LastName { get; init; }

    public required string Phone { get; init; }

    public required string PhoneNormalized { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record SyncOrder
{
    public required Guid Id { get; init; }

    public required int Number { get; init; }

    public required string FirstName { get; init; }

    public string? LastName { get; init; }

    public required string Phone { get; init; }

    public required DateOnly DueDate { get; init; }

    /// <summary>Имя статуса: New, Cooking, Ready, Done, Cancelled.</summary>
    public required string Status { get; init; }

    public string? Comment { get; init; }

    public required long TotalPriceKopecks { get; init; }

    public required long PaidAmountKopecks { get; init; }

    public required long DiscountKopecks { get; init; }

    public Guid? CustomerId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record SyncOrderItem
{
    public required Guid Id { get; init; }

    public required Guid OrderId { get; init; }

    public Guid? RecipeId { get; init; }

    public required string RecipeName { get; init; }

    public required long QuantityMilli { get; init; }

    public required long UnitPriceKopecks { get; init; }

    public required long CostPriceKopecks { get; init; }

    public required bool CostEstimated { get; init; }

    public required long OutputQuantityMilli { get; init; }

    public required string OutputUnit { get; init; }
}

public sealed record SyncPackagingItem
{
    public required Guid Id { get; init; }

    public Guid? CategoryId { get; init; }

    public required string Name { get; init; }

    public string? Size { get; init; }

    public required long PriceKopecks { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record SyncOrderPackaging
{
    public required Guid Id { get; init; }

    public required Guid OrderId { get; init; }

    public Guid? PackagingId { get; init; }

    public required string Name { get; init; }

    public string? Size { get; init; }

    public required long CostKopecks { get; init; }

    public required long SalePriceKopecks { get; init; }

    public required int Quantity { get; init; }

    public required int Position { get; init; }
}
