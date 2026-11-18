using System.Net;
using System.Text;
using System.Text.Json;
using SweetTaste.Core.Domain;
using SweetTaste.Core.Services;
using SweetTaste.Core.Sync;

namespace SweetTaste.Core.Tests;

public class SyncServiceDownloadTests
{
    private static readonly SupabaseOptions Options = new()
    {
        Url = "https://example.supabase.co",
        PublishableKey = "sb_publishable_test",
    };

    private static readonly AuthSession Session = new()
    {
        AccessToken = "access-1",
        RefreshToken = "refresh-1",
        UserId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        Email = "anna@example.ru",
        ExpiresAt = new DateTimeOffset(2026, 11, 18, 15, 0, 0, TimeSpan.Zero),
    };

    private static readonly DateTimeOffset TakenAt = new(2026, 11, 18, 12, 0, 0, TimeSpan.FromHours(3));

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    private static DateOnly Tomorrow => Today.AddDays(1);

    [Fact]
    public async Task Download_ReadsEveryTableInLoadOrder()
    {
        var handler = new TableHandler();
        using var service = new SyncService(Options, new HttpClient(handler));

        await service.DownloadAsync(Session);

        var tables = handler.Requests
            .Select(r => r.Url.Split('/').Last().Split('?')[0])
            .ToList();

        Assert.Equal(SnapshotRows.LoadOrder, tables);
    }

    [Fact]
    public async Task Download_RequestsSelectAllWithSession()
    {
        var handler = new TableHandler();
        using var service = new SyncService(Options, new HttpClient(handler));

        await service.DownloadAsync(Session);

        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.EndsWith("?select=*", request.Url);
            Assert.Equal("sb_publishable_test", request.ApiKey);
            Assert.Equal("Bearer access-1", request.Authorization);
        });
    }

    [Fact]
    public async Task Download_EmptyCloud_ReturnsEmptySnapshot()
    {
        var handler = new TableHandler();
        using var service = new SyncService(Options, new HttpClient(handler));

        var snapshot = await service.DownloadAsync(Session);

        Assert.Equal(0, snapshot.TotalRows);
        Assert.Equal(15, snapshot.SchemaVersion);
        Assert.Empty(snapshot.Orders);
    }

    [Fact]
    public async Task Download_ParsesRowsIntoSnapshot()
    {
        var handler = new TableHandler
        {
            Rows = new Dictionary<string, string>
            {
                [SyncTables.IngredientCategories] = """
                    [{"id":"aaaaaaaa-0000-0000-0000-000000000001","name":"Сыпучие",
                      "created_at":"2026-11-18T09:00:00.000000+00:00"}]
                    """,
                [SyncTables.Ingredients] = """
                    [{"id":"bbbbbbbb-0000-0000-0000-000000000001","name":"Мука","brand":"Макфа",
                      "unit":"г","category_id":"aaaaaaaa-0000-0000-0000-000000000001",
                      "created_at":"2026-11-18T09:00:00.000000+00:00",
                      "updated_at":"2026-11-18T09:00:00.000000+00:00","deleted_at":null}]
                    """,
                [SyncTables.Orders] = """
                    [{"id":"cccccccc-0000-0000-0000-000000000001","number":7,"first_name":"Анна",
                      "last_name":null,"phone":"+7 900 111-22-33","due_date":"2026-11-19",
                      "status":"New","comment":null,"total_price_kopecks":150000,
                      "paid_amount_kopecks":0,"discount_kopecks":0,"customer_id":null,
                      "created_at":"2026-11-18T09:00:00.000000+00:00",
                      "updated_at":"2026-11-18T09:00:00.000000+00:00"}]
                    """,
            },
        };
        using var service = new SyncService(Options, new HttpClient(handler));

        var snapshot = await service.DownloadAsync(Session);

        var category = Assert.Single(snapshot.IngredientCategories);
        Assert.Equal("Сыпучие", category.Name);

        var ingredient = Assert.Single(snapshot.Ingredients);
        Assert.Equal("Мука", ingredient.Name);
        Assert.Equal("Макфа", ingredient.Brand);
        Assert.Equal(category.Id, ingredient.CategoryId);
        Assert.Null(ingredient.DeletedAt);

        var order = Assert.Single(snapshot.Orders);
        Assert.Equal(7, order.Number);
        Assert.Equal(new DateOnly(2026, 11, 19), order.DueDate);
        Assert.Equal(150000, order.TotalPriceKopecks);
        Assert.Null(order.CustomerId);

        Assert.Equal(3, snapshot.TotalRows);
    }

    [Fact]
    public async Task Download_DecodesByteaPhotoToBase64()
    {
        var handler = new TableHandler
        {
            Rows = new Dictionary<string, string>
            {
                [SyncTables.Recipes] = """
                    [{"id":"dddddddd-0000-0000-0000-000000000001","name":"Кекс","category_id":null,
                      "output_quantity_milli":10000,"output_unit":"шт","markup_percent":100,
                      "technology":null,"photo":"\\x0102fa",
                      "created_at":"2026-11-18T09:00:00.000000+00:00",
                      "updated_at":"2026-11-18T09:00:00.000000+00:00","deleted_at":null}]
                    """,
            },
        };
        using var service = new SyncService(Options, new HttpClient(handler));

        var snapshot = await service.DownloadAsync(Session);

        var recipe = Assert.Single(snapshot.Recipes);
        Assert.Equal(Convert.ToBase64String(new byte[] { 1, 2, 250 }), recipe.PhotoBase64);
    }

    [Fact]
    public async Task Download_ReadsCostEstimatedAsBoolean()
    {
        var handler = new TableHandler
        {
            Rows = new Dictionary<string, string>
            {
                [SyncTables.OrderItems] = """
                    [{"id":"eeeeeeee-0000-0000-0000-000000000001",
                      "order_id":"cccccccc-0000-0000-0000-000000000001","recipe_id":null,
                      "recipe_name":"Кекс","quantity_milli":2000,"unit_price_kopecks":50000,
                      "cost_price_kopecks":20000,"cost_estimated":true,
                      "output_quantity_milli":10000,"output_unit":"шт"}]
                    """,
            },
        };
        using var service = new SyncService(Options, new HttpClient(handler));

        var snapshot = await service.DownloadAsync(Session);

        Assert.True(Assert.Single(snapshot.OrderItems).CostEstimated);
    }

    [Fact]
    public async Task Download_ExpiredSession_AsksToSignInAgain()
    {
        var handler = new TableHandler { Status = HttpStatusCode.Unauthorized };
        using var service = new SyncService(Options, new HttpClient(handler));

        var error = await Assert.ThrowsAsync<SyncException>(() => service.DownloadAsync(Session));

        Assert.Equal("Сессия истекла. Войдите заново.", error.Message);
    }

    [Fact]
    public async Task Download_ServerError_NamesTheTable()
    {
        var handler = new TableHandler { Status = HttpStatusCode.BadRequest };
        using var service = new SyncService(Options, new HttpClient(handler));

        var error = await Assert.ThrowsAsync<SyncException>(() => service.DownloadAsync(Session));

        Assert.Equal(SyncTables.IngredientCategories, error.Table);
        Assert.Contains("прочитать", error.Message);
    }

    [Fact]
    public async Task Download_StopsAtFirstFailedTable()
    {
        var handler = new TableHandler { FailOn = SyncTables.Ingredients };
        using var service = new SyncService(Options, new HttpClient(handler));

        await Assert.ThrowsAsync<SyncException>(() => service.DownloadAsync(Session));

        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("/orders"));
    }

    [Fact]
    public async Task Download_RoundTripsExportedSnapshot()
    {
        using var db = new TestDatabase();
        var flour = db.Service.AddIngredient("Мука", "Макфа", Units.Grams, "Сыпучие");
        db.Service.AddPurchase(flour.Id, Today, "Магнит", 1000m, 100m);

        var recipes = new RecipeService(db.Database, db.Service);
        var cake = recipes.CreateRecipe("Кекс", "Выпечка", 10m, RecipeUnits.Pieces, 100,
            new[] { (flour.Id, 500m) }, "Смешать и запечь.");
        recipes.SetRecipePhoto(cake.Id, new byte[] { 1, 2, 250 });

        var orders = new OrderService(db.Database, recipes);
        var order = orders.CreateOrder("Анна", "Смирнова", "+7 900 111-22-33", Tomorrow, null,
            new[] { (cake.Id, 2m) });

        var packaging = new PackagingService(db.Database);
        var box = packaging.CreateItem("Коробка", "20x20", 50m, "Картон");
        orders.SetOrderPackaging(order.Id, new[] { (box.Id, 1) });

        var exported = new SnapshotExporter(db.Database).Export(TakenAt);

        // Облако отдаёт те же строки, что ушли в выгрузку.
        var handler = new TableHandler();
        foreach (var table in SnapshotRows.LoadOrder)
        {
            handler.Rows[table] = SnapshotRows.ToJson(SnapshotRows.For(exported, table));
        }

        using var service = new SyncService(Options, new HttpClient(handler));
        var downloaded = await service.DownloadAsync(Session);

        Assert.Equal(exported.TotalRows, downloaded.TotalRows);
        Assert.Equal(exported.RowCounts, downloaded.RowCounts);

        var recipe = Assert.Single(downloaded.Recipes);
        Assert.Equal("Кекс", recipe.Name);
        Assert.Equal(Convert.ToBase64String(new byte[] { 1, 2, 250 }), recipe.PhotoBase64);
        Assert.Equal("Смешать и запечь.", recipe.Technology);

        var downloadedOrder = Assert.Single(downloaded.Orders);
        Assert.Equal(order.Number, downloadedOrder.Number);

        var exportedOrder = Assert.Single(exported.Orders);
        Assert.Equal(exportedOrder.TotalPriceKopecks, downloadedOrder.TotalPriceKopecks);
    }


    private sealed record CapturedRequest(
        HttpMethod Method,
        string Url,
        string? ApiKey,
        string? Authorization);

    private sealed class TableHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        /// <summary>Таблица, на которой обработчик отвечает ошибкой.</summary>
        public string? FailOn { get; init; }

        public Dictionary<string, string> Rows { get; init; } = new(StringComparer.Ordinal);

        public List<CapturedRequest> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            var apiKey = request.Headers.TryGetValues("apikey", out var keys) ? keys.FirstOrDefault() : null;

            Requests.Add(new CapturedRequest(
                request.Method,
                url,
                apiKey,
                request.Headers.Authorization?.ToString()));

            var table = url.Split('/').Last().Split('?')[0];
            var status = FailOn is not null && table == FailOn ? HttpStatusCode.BadRequest : Status;
            var body = status == Status
                ? Rows.TryGetValue(table, out var rows) ? rows : "[]"
                : """{"message":"invalid input syntax"}""";

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
