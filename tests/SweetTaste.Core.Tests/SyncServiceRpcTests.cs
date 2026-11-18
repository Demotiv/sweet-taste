using System.Net;
using System.Text;
using System.Text.Json;
using SweetTaste.Core.Domain;
using SweetTaste.Core.Services;
using SweetTaste.Core.Sync;

namespace SweetTaste.Core.Tests;

public class SyncServiceRpcTests
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
        HttpStatusCode status = HttpStatusCode.OK,
        string body = "{}")
    {
        var handler = new StubHandler(status, body);
        return (new SyncService(Options, new HttpClient(handler)), handler);
    }

    [Fact]
    public async Task UploadAtomic_SendsSingleRpcCall()
    {
        var (service, handler) = CreateService();
        using var _ = service;

        await service.UploadAtomicAsync(FilledSnapshot(), Session);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://example.supabase.co/rest/v1/rpc/replace_snapshot", request.Url);
    }

    [Fact]
    public async Task UploadAtomic_SendsBearerTokenAndApiKey()
    {
        var (service, handler) = CreateService();
        using var _ = service;

        await service.UploadAtomicAsync(FilledSnapshot(), Session);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("sb_publishable_test", request.ApiKey);
        Assert.Equal("Bearer access-1", request.Authorization);
    }

    [Fact]
    public async Task UploadAtomic_PassesSchemaVersionAndTimestamp()
    {
        var (service, handler) = CreateService();
        using var _ = service;

        await service.UploadAtomicAsync(FilledSnapshot(), Session);

        using var document = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        var root = document.RootElement;

        Assert.Equal(15, root.GetProperty("p_schema_version").GetInt32());
        Assert.Equal("2026-11-18T09:00:00.000000Z", root.GetProperty("p_taken_at").GetString());
    }

    [Fact]
    public async Task UploadAtomic_GroupsRowsByTable()
    {
        var (service, handler) = CreateService();
        using var _ = service;

        await service.UploadAtomicAsync(FilledSnapshot(), Session);

        using var document = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        var tables = document.RootElement.GetProperty("p_tables");

        Assert.Equal(12, tables.EnumerateObject().Count());
        Assert.Equal(1, tables.GetProperty("ingredients").GetArrayLength());
        Assert.Equal(1, tables.GetProperty("orders").GetArrayLength());
        Assert.Equal(1, tables.GetProperty("order_packaging").GetArrayLength());
    }

    [Fact]
    public async Task UploadAtomic_KeepsRowShapeFromSnapshotRows()
    {
        var (service, handler) = CreateService();
        using var _ = service;

        await service.UploadAtomicAsync(FilledSnapshot(), Session);

        using var document = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        var recipe = Assert.Single(
            document.RootElement.GetProperty("p_tables").GetProperty("recipes").EnumerateArray());

        Assert.Equal("Кекс", recipe.GetProperty("name").GetString());
        Assert.Equal("\\x0102fa", recipe.GetProperty("photo").GetString());
        Assert.False(recipe.TryGetProperty("user_id", out var userId));
        Assert.Equal(JsonValueKind.Undefined, userId.ValueKind);
    }

    [Fact]
    public async Task UploadAtomic_ReturnsServerRowCounts()
    {
        var (service, _) = CreateService(
            body: """{"ingredients":1,"orders":1,"order_packaging":1}""");
        using var __ = service;

        var result = await service.UploadAtomicAsync(FilledSnapshot(), Session);

        Assert.Equal(TakenAt, result.TakenAt);
        Assert.Equal(1, result.RowCounts[SyncTables.Ingredients]);
        Assert.Equal(1, result.RowCounts[SyncTables.Orders]);
    }

    [Fact]
    public async Task UploadAtomic_ExpiredSession_AsksToSignInAgain()
    {
        var (service, _) = CreateService(HttpStatusCode.Unauthorized, """{"message":"JWT expired"}""");
        using var __ = service;

        var error = await Assert.ThrowsAsync<SyncException>(
            () => service.UploadAtomicAsync(FilledSnapshot(), Session));

        Assert.Equal("Сессия истекла. Войдите заново.", error.Message);
    }

    [Fact]
    public async Task UploadAtomic_ServerError_ShowsServerMessage()
    {
        var (service, _) = CreateService(
            HttpStatusCode.BadRequest,
            """{"message":"replace_snapshot: p_tables должен быть объектом"}""");
        using var __ = service;

        var error = await Assert.ThrowsAsync<SyncException>(
            () => service.UploadAtomicAsync(FilledSnapshot(), Session));

        Assert.Contains("p_tables должен быть объектом", error.Message);
    }

    [Fact]
    public async Task UploadAtomic_EmptySnapshot_SendsEmptyArrays()
    {
        var (service, handler) = CreateService();
        using var _ = service;

        var empty = new SyncSnapshot
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

        await service.UploadAtomicAsync(empty, Session);

        using var document = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        var tables = document.RootElement.GetProperty("p_tables");

        Assert.Equal(12, tables.EnumerateObject().Count());
        Assert.All(tables.EnumerateObject(), table => Assert.Equal(0, table.Value.GetArrayLength()));
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        string Url,
        string? ApiKey,
        string? Authorization,
        string Body);

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public List<CapturedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var apiKey = request.Headers.TryGetValues("apikey", out var keys) ? keys.FirstOrDefault() : null;

            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri!.ToString(),
                apiKey,
                request.Headers.Authorization?.ToString(),
                body));

            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
