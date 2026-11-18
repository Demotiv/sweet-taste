using System.Net;
using System.Text;
using System.Text.Json;
using SweetTaste.Core.Domain;
using SweetTaste.Core.Services;
using SweetTaste.Core.Sync;

namespace SweetTaste.Core.Tests;

public class SyncServiceTests
{
    private static readonly SupabaseOptions Options = new()
    {
        Url = "https://example.supabase.co",
        PublishableKey = "sb_publishable_test",
    };

    private static readonly Guid UserId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static readonly AuthSession Session = new()
    {
        AccessToken = "access-1",
        RefreshToken = "refresh-1",
        UserId = UserId,
        Email = "anna@example.ru",
        ExpiresAt = new DateTimeOffset(2026, 11, 18, 15, 0, 0, TimeSpan.Zero),
    };

    private static readonly DateTimeOffset TakenAt = new(2026, 11, 18, 12, 0, 0, TimeSpan.FromHours(3));

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    private static DateOnly Tomorrow => Today.AddDays(1);

    private static SyncSnapshot EmptySnapshot() => new()
    {
        SchemaVersion = 15,
        TakenAt = TakenAt,
        IngredientCategories = Array.Empty<SyncIngredientCategory>(),
        RecipeCategories = Array.Empty<SyncRecipeCategory>(),
        PackagingCategories = Array.Empty<SyncPackagingCategory>(),
        Ingredients = Array.Empty<SyncIngredient>(),
        Purchases = Array.Empty<SyncPurchase>(),
        Recipes = Array.Empty<SyncRecipe>(),
        RecipeIngredients = Array.Empty<SyncRecipeIngredient>(),
        Customers = Array.Empty<SyncCustomer>(),
        Orders = Array.Empty<SyncOrder>(),
        OrderItems = Array.Empty<SyncOrderItem>(),
        PackagingItems = Array.Empty<SyncPackagingItem>(),
        OrderPackaging = Array.Empty<SyncOrderPackaging>(),
    };

    private static SyncSnapshot FilledSnapshot()
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

        return new SnapshotExporter(db.Database).Export(TakenAt);
    }

    private static (SyncService Service, StubHandler Handler) CreateService(
        HttpStatusCode status = HttpStatusCode.Created)
    {
        var handler = new StubHandler(status);
        return (new SyncService(Options, new HttpClient(handler)), handler);
    }

    [Fact]
    public async Task Upload_ClearsEveryTableBeforeInserting()
    {
        var (service, handler) = CreateService();
        using var _ = service;

        await service.UploadAsync(EmptySnapshot(), Session);

        var deletes = handler.Requests.Where(r => r.Method == HttpMethod.Delete).ToList();
        Assert.Equal(12, deletes.Count);
        Assert.All(deletes, r => Assert.Contains("?id=not.is.null", r.Url));
    }

    [Fact]
    public async Task Upload_DeletesInReverseDependencyOrder()
    {
        var (service, handler) = CreateService();
        using var _ = service;

        await service.UploadAsync(EmptySnapshot(), Session);

        var deleted = handler.Requests
            .Where(r => r.Method == HttpMethod.Delete)
            .Select(r => r.Url.Split('/').Last().Split('?')[0])
            .ToList();

        Assert.Equal(SnapshotRows.LoadOrder.Reverse(), deleted);
        Assert.Equal("order_packaging", deleted[0]);
        Assert.Equal("ingredient_categories", deleted[^1]);
    }

    [Fact]
    public async Task Upload_SendsBearerTokenAndApiKey()
    {
        var (service, handler) = CreateService();
        using var _ = service;

        await service.UploadAsync(EmptySnapshot(), Session);

        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("sb_publishable_test", request.ApiKey);
            Assert.Equal("Bearer access-1", request.Authorization);
        });
    }

    [Fact]
    public async Task Upload_InsertsRowsInDependencyOrder()
    {
        var (service, handler) = CreateService();
        using var _ = service;

        await service.UploadAsync(FilledSnapshot(), Session);

        var inserted = handler.Requests
            .Where(r => r.Method == HttpMethod.Post && !r.Url.EndsWith("/sync_state"))
            .Select(r => r.Url.Split('/').Last())
            .ToList();

        Assert.Equal(SnapshotRows.LoadOrder, inserted);
    }

    [Fact]
    public async Task Upload_SkipsEmptyTables()
    {
        var (service, handler) = CreateService();
        using var _ = service;

        await service.UploadAsync(EmptySnapshot(), Session);

        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post && !r.Url.EndsWith("/sync_state"));
    }

    [Fact]
    public async Task Upload_SendsRowsAsJsonArray()
    {
        var (service, handler) = CreateService();
        using var _ = service;

        await service.UploadAsync(FilledSnapshot(), Session);

        var ingredients = handler.Requests.Single(r => r.Url.EndsWith("/ingredients") && r.Method == HttpMethod.Post);
        using var document = JsonDocument.Parse(ingredients.Body);
        var row = Assert.Single(document.RootElement.EnumerateArray());

        Assert.Equal("Мука", row.GetProperty("name").GetString());
        Assert.Equal("Макфа", row.GetProperty("brand").GetString());
        Assert.Equal("г", row.GetProperty("unit").GetString());
        Assert.True(Guid.TryParse(row.GetProperty("category_id").GetString(), out var categoryId));
        Assert.NotEqual(Guid.Empty, categoryId);
        Assert.False(row.TryGetProperty("user_id", out var userId));
        Assert.Equal(JsonValueKind.Undefined, userId.ValueKind);
    }

    [Fact]
    public async Task Upload_EncodesPhotoAsByteaHex()
    {
        var (service, handler) = CreateService();
        using var _ = service;

        await service.UploadAsync(FilledSnapshot(), Session);

        var recipes = handler.Requests.Single(r => r.Url.EndsWith("/recipes") && r.Method == HttpMethod.Post);
        using var document = JsonDocument.Parse(recipes.Body);
        var row = Assert.Single(document.RootElement.EnumerateArray());

        Assert.Equal("\\x0102fa", row.GetProperty("photo").GetString());
    }

    [Fact]
    public async Task Upload_SendsCostEstimatedAsBoolean()
    {
        var (service, handler) = CreateService();
        using var _ = service;

        await service.UploadAsync(FilledSnapshot(), Session);

        var items = handler.Requests.Single(r => r.Url.EndsWith("/order_items") && r.Method == HttpMethod.Post);
        using var document = JsonDocument.Parse(items.Body);
        var row = Assert.Single(document.RootElement.EnumerateArray());

        Assert.Equal(JsonValueKind.False, row.GetProperty("cost_estimated").ValueKind);
    }

    [Fact]
    public async Task Upload_WritesSyncStateWithRowCounts()
    {
        var (service, handler) = CreateService();
        using var _ = service;

        var snapshot = FilledSnapshot();
        await service.UploadAsync(snapshot, Session);

        var state = handler.Requests.Single(r => r.Url.EndsWith("/sync_state"));
        Assert.Equal("resolution=merge-duplicates,return=minimal", state.Prefer);

        using var document = JsonDocument.Parse(state.Body);
        var row = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal(UserId.ToString(), row.GetProperty("user_id").GetString());
        Assert.Equal(15, row.GetProperty("schema_version").GetInt32());
        Assert.Equal(1, row.GetProperty("row_counts").GetProperty("ingredients").GetInt32());
    }

    [Fact]
    public async Task Upload_ReturnsRowCounts()
    {
        var (service, handler) = CreateService();
        using var _ = service;

        var result = await service.UploadAsync(FilledSnapshot(), Session);

        Assert.Equal(TakenAt, result.TakenAt);
        Assert.Equal(12, result.TotalRows);
        Assert.Equal(1, result.RowCounts[SyncTables.Orders]);
        Assert.NotEmpty(handler.Requests);
    }
    [Fact]
    public async Task Upload_ExpiredSession_AsksToSignInAgain()
    {
        var (service, _) = CreateService(HttpStatusCode.Unauthorized);
        using var __ = service;

        var error = await Assert.ThrowsAsync<SyncException>(
            () => service.UploadAsync(EmptySnapshot(), Session));

        Assert.Equal("Сессия истекла. Войдите заново.", error.Message);
    }

    [Fact]
    public async Task Upload_ServerError_NamesTheTable()
    {
        var (service, _) = CreateService(HttpStatusCode.BadRequest);
        using var __ = service;

        var error = await Assert.ThrowsAsync<SyncException>(
            () => service.UploadAsync(EmptySnapshot(), Session));

        Assert.Equal("order_packaging", error.Table);
        Assert.Contains("order_packaging", error.Message);
    }

    [Fact]
    public async Task Upload_StopsAtFirstFailedTable()
    {
        var handler = new StubHandler(HttpStatusCode.Created)
        {
            FailOn = "ingredients",
        };
        using var service = new SyncService(Options, new HttpClient(handler));

        await Assert.ThrowsAsync<SyncException>(() => service.UploadAsync(FilledSnapshot(), Session));

        Assert.DoesNotContain(handler.Requests, r => r.Url.EndsWith("/orders") && r.Method == HttpMethod.Post);
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        string Url,
        string? ApiKey,
        string? Authorization,
        string? Prefer,
        string Body);

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;

        public StubHandler(HttpStatusCode status)
        {
            _status = status;
        }

        /// <summary>Таблица, на которой обработчик отвечает ошибкой.</summary>
        public string? FailOn { get; init; }

        public List<CapturedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var apiKey = request.Headers.TryGetValues("apikey", out var keys) ? keys.FirstOrDefault() : null;
            var prefer = request.Headers.TryGetValues("Prefer", out var prefers) ? prefers.FirstOrDefault() : null;
            var url = request.RequestUri!.ToString();

            Requests.Add(new CapturedRequest(
                request.Method,
                url,
                apiKey,
                request.Headers.Authorization?.ToString(),
                prefer,
                body));

            var table = url.Split('/').Last().Split('?')[0];
            var status = FailOn is not null && table == FailOn ? HttpStatusCode.BadRequest : _status;

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(
                    status == _status ? "{}" : """{"message":"invalid input syntax"}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }
}
