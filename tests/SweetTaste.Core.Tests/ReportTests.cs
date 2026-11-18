using Microsoft.Data.Sqlite;
using SweetTaste.Core.Domain;
using SweetTaste.Core.Services;
using SweetTaste.Core.Storage;

namespace SweetTaste.Core.Tests;

public class ReportTests
{
    private static (TestDatabase Db, RecipeService Recipes, OrderService Orders, ReportService Reports,
        Guid Cake, Guid Cookie) CreateBase()
    {
        var db = new TestDatabase();
        var flour = db.Service.AddIngredient("Мука", null, Units.Grams);
        var sugar = db.Service.AddIngredient("Сахар", null, Units.Grams);
        var butter = db.Service.AddIngredient("Масло", null, Units.Grams);

        var today = DateOnly.FromDateTime(DateTime.Now);
        db.Service.AddPurchase(flour.Id, today, "Магнит", 1000m, 100m);   // 0.10 ₽/г
        db.Service.AddPurchase(sugar.Id, today, "Магнит", 1000m, 200m);   // 0.20 ₽/г
        db.Service.AddPurchase(butter.Id, today, "Магнит", 1000m, 1000m); // 1.00 ₽/г

        var recipes = new RecipeService(db.Database, db.Service);
        var cake = recipes.CreateRecipe("Кекс", null, 10, RecipeUnits.Pieces, 100,
            new[] { (flour.Id, 500m), (sugar.Id, 200m), (butter.Id, 100m) });
        var cookie = recipes.CreateRecipe("Печенье", null, 5, RecipeUnits.Pieces, 100,
            new[] { (flour.Id, 100m), (sugar.Id, 50m) });

        var orders = new OrderService(db.Database, recipes);
        var reports = new ReportService(orders, db.Service);
        return (db, recipes, orders, reports, cake.Id, cookie.Id);
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    private static DateOnly Tomorrow => Today.AddDays(1);

    private static Guid Issue(OrderService orders, Guid orderId)
    {
        orders.AdvanceOrder(orderId);
        orders.AdvanceOrder(orderId);
        orders.AdvanceOrder(orderId);
        return orderId;
    }

    [Fact]
    public void Build_CountsOnlyIssuedOrdersInPeriod()
    {
        var (db, recipes, orders, reports, cake, cookie) = CreateBase();
        using var _ = db;

        Issue(orders, orders.CreateOrder("Иван", null, "1", Tomorrow, null, new[] { (cake, 2m) }).Id);
        orders.CreateOrder("Анна", null, "2", Tomorrow, null, new[] { (cookie, 1m) });
        Issue(orders, orders.CreateOrder("Пётр", null, "3", Tomorrow.AddDays(10), null, new[] { (cake, 1m) }).Id);
        orders.CancelOrder(orders.CreateOrder("Олег", null, "4", Tomorrow, null, new[] { (cake, 1m) }).Id);

        var report = reports.Build(Today, Today.AddDays(7));

        Assert.Equal(1, report.OrdersCount);
        Assert.Equal(76m, report.Revenue);
        Assert.Equal(38m, report.Cost);
        Assert.Equal(38m, report.Profit);
        Assert.Equal(76m, report.AverageCheck);
        Assert.Equal(1, report.InProgressCount);
        Assert.Equal(8m, report.InProgressRevenue);
        Assert.False(report.HasEstimatedCost);
        Assert.False(report.IsEmpty);

        var day = Assert.Single(report.Days);
        Assert.Equal(Tomorrow, day.Date);
        Assert.Equal(1, day.OrdersCount);
        Assert.Equal(76m, day.Revenue);

        var top = Assert.Single(report.TopItems);
        Assert.Equal("Кекс", top.Name);
        Assert.Equal(2m, top.Quantity);
        Assert.Equal(76m, top.Revenue);
        Assert.Equal(38m, top.Profit);
    }

    [Fact]
    public void Build_UsesCostSnapshotAndIgnoresLaterPriceChanges()
    {
        var (db, recipes, orders, reports, cake, cookie) = CreateBase();
        using var _ = db;

        Issue(orders, orders.CreateOrder("Иван", null, "1", Tomorrow, null, new[] { (cake, 2m) }).Id);

        Assert.Equal(38m, reports.Build(Today, Today.AddDays(7)).Profit);

        var flourId = db.Service.GetIngredients().Single(item => item.Ingredient.Name == "Мука").Ingredient.Id;
        db.Service.AddPurchase(flourId, Today, "Магнит", 1000m, 500m); // мука подорожала

        var afterPriceChange = reports.Build(Today, Today.AddDays(7));
        Assert.Equal(38m, afterPriceChange.Profit);
        Assert.Equal(38m, afterPriceChange.Cost);

        Issue(orders, orders.CreateOrder("Анна", null, "2", Tomorrow, null, new[] { (cake, 2m) }).Id);
        var withNewOrder = reports.Build(Today, Today.AddDays(7));

        Assert.Equal(192m, withNewOrder.Revenue);
        Assert.Equal(96m, withNewOrder.Cost); // 38 за первый заказ + 29 × 2 за новый
        Assert.Equal(96m, withNewOrder.Profit);
    }

    [Fact]
    public void Build_CountsPurchasesInsidePeriod()
    {
        var (db, recipes, orders, reports, cake, cookie) = CreateBase();
        using var _ = db;

        var flourId = db.Service.GetIngredients().Single(item => item.Ingredient.Name == "Мука").Ingredient.Id;
        db.Service.AddPurchase(flourId, Today.AddDays(-1), "Лента", 1000m, 500m);

        var month = reports.Build(Today, Today.AddDays(7));
        Assert.Equal(3, month.PurchasesCount);
        Assert.Equal(1300m, month.PurchasesCost);

        var wide = reports.Build(Today.AddDays(-2), Today.AddDays(7));
        Assert.Equal(4, wide.PurchasesCount);
        Assert.Equal(1800m, wide.PurchasesCost);
    }

    [Fact]
    public void Build_AggregatesTopItemsByRecipeName()
    {
        var (db, recipes, orders, reports, cake, cookie) = CreateBase();
        using var _ = db;

        Issue(orders, orders.CreateOrder("Иван", null, "1", Tomorrow, null,
            new[] { (cake, 2m), (cookie, 3m) }).Id);

        var report = reports.Build(Today, Today.AddDays(7));

        Assert.Equal(2, report.TopItems.Count);
        Assert.Equal("Печенье", report.TopItems[0].Name);
        Assert.Equal(3m, report.TopItems[0].Quantity);
        Assert.Equal(24m, report.TopItems[0].Revenue);
        Assert.Equal(12m, report.TopItems[0].Cost);
        Assert.Equal(12m, report.TopItems[0].Profit);
        Assert.Equal("Кекс", report.TopItems[1].Name);
        Assert.Equal(76m, report.TopItems[1].Revenue);
    }

    [Fact]
    public void Build_EmptyPeriodHasZeroTotals()
    {
        var (db, recipes, orders, reports, cake, cookie) = CreateBase();
        using var _ = db;

        var report = reports.Build(Today.AddDays(-10), Today.AddDays(-5));

        Assert.Equal(0, report.OrdersCount);
        Assert.Equal(0m, report.Revenue);
        Assert.Equal(0m, report.AverageCheck);
        Assert.Empty(report.TopItems);
        Assert.Empty(report.Days);
        Assert.True(report.IsEmpty);
    }

    [Fact]
    public void Build_RejectsReversedPeriod()
    {
        var (db, recipes, orders, reports, cake, cookie) = CreateBase();
        using var _ = db;

        Assert.Throws<ValidationException>(() => reports.Build(Tomorrow, Today));
    }

    [Fact]
    public void Build_MarksOrdersWithApproximateCost()
    {
        var (db, recipes, orders, reports, cake, cookie) = CreateBase();
        using var _ = db;

        var order = orders.CreateOrder("Иван", null, "1", Tomorrow, null, new[] { (cake, 1m) });
        Issue(orders, order.Id);

        using (var connection = db.Database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE order_items SET cost_estimated = 1;";
            command.ExecuteNonQuery();
        }

        Assert.True(reports.Build(Today, Today.AddDays(7)).HasEstimatedCost);
    }

    [Fact]
    public void Migration_ConvertsPortionsAndKeepsQuantities()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sweet-taste-v7-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "old.db");

        try
        {
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                   {
                       DataSource = path,
                       Pooling = false,
                       ForeignKeys = true,
                   }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE recipe_categories (id TEXT NOT NULL PRIMARY KEY, name TEXT NOT NULL, created_at TEXT NOT NULL);
                    CREATE TABLE ingredient_categories (id TEXT NOT NULL PRIMARY KEY);
                    CREATE TABLE ingredients (id TEXT NOT NULL PRIMARY KEY);
                    CREATE TABLE purchases (id TEXT NOT NULL PRIMARY KEY);
                    CREATE TABLE recipe_ingredients (
                        recipe_id TEXT NOT NULL,
                        ingredient_id TEXT NOT NULL,
                        amount_milli INTEGER NOT NULL CHECK (amount_milli > 0),
                        position INTEGER NOT NULL,
                        PRIMARY KEY (recipe_id, ingredient_id)
                    );
                    CREATE TABLE recipes (
                        id TEXT NOT NULL PRIMARY KEY,
                        name TEXT NOT NULL,
                        category_id TEXT NULL,
                        output_quantity INTEGER NOT NULL CHECK (output_quantity > 0),
                        output_unit TEXT NOT NULL,
                        markup_percent INTEGER NOT NULL CHECK (markup_percent >= 0),
                        created_at TEXT NOT NULL,
                        updated_at TEXT NOT NULL,
                        deleted_at TEXT NULL,
                        FOREIGN KEY (category_id) REFERENCES recipe_categories(id) ON DELETE SET NULL
                    );
                    CREATE INDEX ix_recipes_name ON recipes(name);
                    CREATE INDEX ix_recipes_deleted_at ON recipes(deleted_at);
                    CREATE TABLE orders (id TEXT NOT NULL PRIMARY KEY, first_name TEXT NOT NULL DEFAULT '', last_name TEXT NULL, phone TEXT NOT NULL DEFAULT '', created_at TEXT NOT NULL DEFAULT '', status TEXT NOT NULL DEFAULT 'New', total_price_kopecks INTEGER NOT NULL DEFAULT 0);
                    CREATE TABLE order_items (
                        id TEXT NOT NULL PRIMARY KEY,
                        order_id TEXT NOT NULL,
                        recipe_id TEXT NULL,
                        recipe_name TEXT NOT NULL,
                        quantity INTEGER NOT NULL CHECK (quantity > 0),
                        unit_price_kopecks INTEGER NOT NULL,
                        cost_price_kopecks INTEGER NOT NULL DEFAULT 0,
                        cost_estimated INTEGER NOT NULL DEFAULT 1,
                        output_quantity INTEGER NOT NULL,
                        output_unit TEXT NOT NULL,
                        FOREIGN KEY (order_id) REFERENCES orders(id) ON DELETE CASCADE,
                        FOREIGN KEY (recipe_id) REFERENCES recipes(id) ON DELETE SET NULL
                    );
                    CREATE INDEX ix_order_items_order ON order_items(order_id);

                    INSERT INTO recipes VALUES
                        ('r1', 'Торт', NULL, 2, 'порция', 100, '2026-01-01T00:00:00+00:00', '2026-01-01T00:00:00+00:00', NULL);
                    INSERT INTO orders (id, first_name, phone, created_at) VALUES
                        ('11111111-1111-1111-1111-111111111111', 'Иван', '123', '2026-01-01T00:00:00+00:00');
                    INSERT INTO order_items VALUES
                        ('i1', '11111111-1111-1111-1111-111111111111', 'r1', 'Торт', 3, 50000, 20000, 0, 2, 'порция');

                    PRAGMA user_version = 6;
                    """;
                command.ExecuteNonQuery();
            }

            new AppDatabase(path).Initialize();

            using var check = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Pooling = false,
            }.ToString());
            check.Open();

            using (var recipe = check.CreateCommand())
            {
                recipe.CommandText = "SELECT output_quantity_milli, output_unit FROM recipes;";
                using var reader = recipe.ExecuteReader();
                Assert.True(reader.Read());
                Assert.Equal(2000L, reader.GetInt64(0));
                Assert.Equal("шт", reader.GetString(1));
            }

            using (var item = check.CreateCommand())
            {
                item.CommandText = "SELECT quantity_milli, output_quantity_milli, output_unit FROM order_items;";
                using var reader = item.ExecuteReader();
                Assert.True(reader.Read());
                Assert.Equal(3000L, reader.GetInt64(0));
                Assert.Equal(2000L, reader.GetInt64(1));
                Assert.Equal("шт", reader.GetString(2));
            }

            using (var fk = check.CreateCommand())
            {
                fk.CommandText = "PRAGMA foreign_key_check;";
                using var reader = fk.ExecuteReader();
                Assert.False(reader.Read());
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
