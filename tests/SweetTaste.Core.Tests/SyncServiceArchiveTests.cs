using System.Net;
using System.Text;
using System.Text.Json;
using SweetTaste.Core.Sync;

namespace SweetTaste.Core.Tests;

public class SyncServiceArchiveTests
{
    private const string ArchiveRpcPath = "/rest/v1/rpc/archive_snapshot";

    private const string ArchiveTablePath = "/rest/v1/snapshot_archive";

    private const string ReplaceRpcPath = "/rest/v1/rpc/replace_snapshot";

    private const string StatePath = "/rest/v1/sync_state";

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

    private static readonly Guid ArchiveId = Guid.Parse("cccccccc-0000-0000-0000-000000000003");

    [Fact]
    public async Task Archive_SendsRpcCallWithKeep()
    {
        var (service, handler) = CreateService(
            ArchiveRpcPath,
            """{"archived":false,"reason":"duplicate"}""");
        using var _ = service;

        await service.ArchiveAsync(Session);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://example.supabase.co/rest/v1/rpc/archive_snapshot", request.Url);
        Assert.Equal("sb_publishable_test", request.ApiKey);
        Assert.Equal("Bearer access-1", request.Authorization);

        using var document = JsonDocument.Parse(request.Body);
        Assert.Equal(10, document.RootElement.GetProperty("p_keep").GetInt32());
    }

    [Fact]
    public async Task Archive_ParsesArchivedResult()
    {
        var id = Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444");
        var (service, _) = CreateService(
            ArchiveRpcPath,
            $$$"""
                {"archived":true,"id":"{{{id}}}","taken_at":"2026-11-18T09:00:00.000000+00:00",
                 "schema_version":15,"total_rows":59,"row_counts":{"orders":17}}
                """);
        using var __ = service;

        var result = await service.ArchiveAsync(Session);

        Assert.True(result.Archived);
        Assert.Equal(id, result.Id);
        Assert.Equal(new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero), result.TakenAt);
        Assert.Equal(59, result.TotalRows);
        Assert.Null(result.Reason);
    }

    [Fact]
    public async Task Archive_ParsesSkippedResult()
    {
        var (service, _) = CreateService(
            ArchiveRpcPath,
            """{"archived":false,"reason":"empty"}""");
        using var __ = service;

        var result = await service.ArchiveAsync(Session);

        Assert.False(result.Archived);
        Assert.Equal("empty", result.Reason);
        Assert.Null(result.Id);
        Assert.Null(result.TakenAt);
    }

    [Fact]
    public async Task Archive_Unauthorized_AsksToSignIn()
    {
        var (service, _) = CreateService(
            ArchiveRpcPath,
            """{"message":"JWT expired"}""",
            HttpStatusCode.Unauthorized);
        using var __ = service;

        var error = await Assert.ThrowsAsync<SyncException>(() => service.ArchiveAsync(Session));

        Assert.Equal("Сессия истекла. Войдите заново.", error.Message);
    }

    [Fact]
    public async Task Archive_ServerError_ShowsServerMessage()
    {
        var (service, _) = CreateService(
            ArchiveRpcPath,
            """{"message":"archive_snapshot: p_keep должен быть числом"}""",
            HttpStatusCode.BadRequest);
        using var __ = service;

        var error = await Assert.ThrowsAsync<SyncException>(() => service.ArchiveAsync(Session));

        Assert.Contains("p_keep должен быть числом", error.Message);
    }

    [Fact]
    public async Task List_RequestsMetadataOnly()
    {
        var (service, handler) = CreateService(ArchiveTablePath, "[]");
        using var _ = service;

        await service.ListArchivesAsync(Session);

        var url = Assert.Single(handler.Requests).Url;
        Assert.Contains("select=id,taken_at,schema_version,total_rows,archived_at", url);
        Assert.Contains("order=archived_at.desc", url);
        Assert.Contains("limit=20", url);
        Assert.DoesNotContain("tables", url);
    }

    [Fact]
    public async Task List_ParsesEntries()
    {
        var (service, _) = CreateService(
            ArchiveTablePath,
            """
                [
                  {"id":"aaaaaaaa-0000-0000-0000-000000000001",
                   "taken_at":"2026-11-18T09:00:00.000000+00:00","schema_version":15,
                   "total_rows":59,"archived_at":"2026-11-18T10:00:00.000000+00:00"},
                  {"id":"bbbbbbbb-0000-0000-0000-000000000002",
                   "taken_at":"2026-11-17T09:00:00.000000+00:00","schema_version":15,
                   "total_rows":42,"archived_at":"2026-11-17T10:00:00.000000+00:00"}
                ]
                """);
        using var __ = service;

        var archives = await service.ListArchivesAsync(Session);

        Assert.Equal(2, archives.Count);
        Assert.Equal(59, archives[0].TotalRows);
        Assert.Equal(new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero), archives[0].TakenAt);
        Assert.Equal(new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero), archives[0].ArchivedAt);
        Assert.Equal(42, archives[1].TotalRows);
    }

    [Fact]
    public async Task List_Empty_ReturnsEmpty()
    {
        var (service, _) = CreateService(ArchiveTablePath, "[]");
        using var __ = service;

        var archives = await service.ListArchivesAsync(Session);

        Assert.Empty(archives);
    }

    [Fact]
    public async Task DownloadArchive_RequestsPayloadById()
    {
        var (service, handler) = CreateService(
            ArchiveTablePath,
            """[{"schema_version":15,"taken_at":"2026-11-18T09:00:00.000000+00:00","tables":{}}]""");
        using var _ = service;

        await service.DownloadArchiveAsync(ArchiveId, Session);

        var url = Assert.Single(handler.Requests).Url;
        Assert.Contains("select=schema_version,taken_at,tables", url);
        Assert.Contains($"id=eq.{ArchiveId:D}", url);
        Assert.Contains("limit=1", url);
    }

    [Fact]
    public async Task DownloadArchive_UsesArchivedSchemaVersion()
    {
        var (service, _) = CreateService(
            ArchiveTablePath,
            """[{"schema_version":14,"taken_at":"2026-11-18T09:00:00.000000+00:00","tables":{}}]""");
        using var __ = service;

        var snapshot = await service.DownloadArchiveAsync(ArchiveId, Session);

        Assert.Equal(14, snapshot.SchemaVersion);
        Assert.Equal(0, snapshot.TotalRows);
    }

    [Fact]
    public async Task DownloadArchive_ParsesRowsAndPhoto()
    {
        var (service, _) = CreateService(
            ArchiveTablePath,
            """
                [{"schema_version":15,"taken_at":"2026-11-18T09:00:00.000000+00:00","tables":{
                  "recipes":[{"id":"dddddddd-0000-0000-0000-000000000001","name":"Кекс",
                    "category_id":null,"output_quantity_milli":10000,"output_unit":"шт",
                    "markup_percent":100,"technology":null,"photo":"\\x0102fa",
                    "created_at":"2026-11-18T09:00:00.000000+00:00",
                    "updated_at":"2026-11-18T09:00:00.000000+00:00","deleted_at":null}],
                  "orders":[
                    {"id":"cccccccc-0000-0000-0000-000000000001","number":7,"first_name":"Анна",
                     "last_name":null,"phone":"+7 900 111-22-33","due_date":"2026-11-19",
                     "status":"New","comment":null,"total_price_kopecks":150000,
                     "paid_amount_kopecks":0,"discount_kopecks":0,"customer_id":null,
                     "created_at":"2026-11-18T09:00:00.000000+00:00",
                     "updated_at":"2026-11-18T09:00:00.000000+00:00"},
                    {"id":"cccccccc-0000-0000-0000-000000000002","number":8,"first_name":"Пётр",
                     "last_name":"Сидоров","phone":"+7 900 222-33-44","due_date":"2026-11-20",
                     "status":"Ready","comment":null,"total_price_kopecks":90000,
                     "paid_amount_kopecks":45000,"discount_kopecks":0,"customer_id":null,
                     "created_at":"2026-11-18T09:00:00.000000+00:00",
                     "updated_at":"2026-11-18T09:00:00.000000+00:00"}
                  ]}}]
                """);
        using var __ = service;

        var snapshot = await service.DownloadArchiveAsync(ArchiveId, Session);

        Assert.Equal(new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero), snapshot.TakenAt);

        var recipe = Assert.Single(snapshot.Recipes);
        Assert.Equal("Кекс", recipe.Name);
        Assert.Equal(Convert.ToBase64String(new byte[] { 1, 2, 250 }), recipe.PhotoBase64);

        Assert.Equal(2, snapshot.Orders.Count);
        Assert.Equal(new DateOnly(2026, 11, 19), snapshot.Orders[0].DueDate);
        Assert.Equal(new DateOnly(2026, 11, 20), snapshot.Orders[1].DueDate);
        Assert.Equal(
            new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero),
            snapshot.Orders[0].CreatedAt);
        Assert.Equal(3, snapshot.TotalRows);
    }

    [Fact]
    public async Task DownloadArchive_NotFound_Throws()
    {
        var (service, _) = CreateService(ArchiveTablePath, "[]");
        using var __ = service;

        var error = await Assert.ThrowsAsync<SyncException>(
            () => service.DownloadArchiveAsync(ArchiveId, Session));

        Assert.Contains("не найден", error.Message);
    }

    [Fact]
    public async Task ReadCloudState_ReturnsCounts()
    {
        var (service, handler) = CreateService(
            StatePath,
            """
                [{"schema_version":15,"last_snapshot_at":"2026-11-18T09:00:00.000000+00:00",
                  "row_counts":{"orders":17,"order_items":20,"customers":4,"ingredients":10,
                  "purchases":5,"recipes":2,"packaging_items":1}}]
                """);
        using var _ = service;

        var state = Assert.IsType<CloudState>(await service.ReadCloudStateAsync(Session));

        Assert.Equal(59, state.TotalRows);
        Assert.Equal(17, state.RowCounts["orders"]);
        Assert.Equal(15, state.SchemaVersion);
        Assert.Equal(new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero), state.TakenAt);

        var url = Assert.Single(handler.Requests).Url;
        Assert.Contains("select=schema_version,last_snapshot_at,row_counts", url);
    }

    [Fact]
    public async Task ReadCloudState_Empty_ReturnsNull()
    {
        var (service, _) = CreateService(StatePath, "[]");
        using var __ = service;

        Assert.Null(await service.ReadCloudStateAsync(Session));
    }

    [Fact]
    public async Task Read_ArchiveShapedJson_MatchesSnapshotRows()
    {
        var (service, _) = CreateService(
            ArchiveTablePath,
            """
                [{"schema_version":15,"taken_at":"2026-11-18T09:00:00.000000+00:00","tables":{
                  "recipes":[{"id":"dddddddd-0000-0000-0000-000000000001","name":"Кекс",
                    "category_id":null,"output_quantity_milli":10000,"output_unit":"шт",
                    "markup_percent":100,"technology":"Смешать и запечь.","photo":"\\x0102fa",
                    "created_at":"2026-11-18T09:00:00.000000+00:00",
                    "updated_at":"2026-11-18T09:00:00.000000+00:00","deleted_at":null}],
                  "orders":[{"id":"cccccccc-0000-0000-0000-000000000001","number":7,
                    "first_name":"Анна","last_name":null,"phone":"+7 900 111-22-33",
                    "due_date":"2026-11-19","status":"New","comment":null,
                    "total_price_kopecks":150000,"paid_amount_kopecks":0,"discount_kopecks":0,
                    "customer_id":null,"created_at":"2026-11-18T09:00:00.000000+00:00",
                    "updated_at":"2026-11-18T09:00:00.000000+00:00"}],
                  "order_items":[{"id":"eeeeeeee-0000-0000-0000-000000000001",
                    "order_id":"cccccccc-0000-0000-0000-000000000001",
                    "recipe_id":"dddddddd-0000-0000-0000-000000000001","recipe_name":"Кекс",
                    "quantity_milli":2000,"unit_price_kopecks":50000,
                    "cost_price_kopecks":20000,"cost_estimated":true,
                    "output_quantity_milli":10000,"output_unit":"шт"}]
                }}]
                """);
        using var __ = service;

        var snapshot = await service.DownloadArchiveAsync(ArchiveId, Session);

        // Архив собран серверным to_jsonb, но форма обязана совпасть с той,
        // что клиент выгружает: иначе восстановление потеряет фото и флаги.
        var recipe = Assert.Single(SnapshotRows.For(snapshot, SyncTables.Recipes));
        Assert.Equal("\\x0102fa", recipe["photo"]);
        Assert.Equal("Смешать и запечь.", recipe["technology"]);

        var item = Assert.Single(SnapshotRows.For(snapshot, SyncTables.OrderItems));
        Assert.Equal(true, item["cost_estimated"]);
        Assert.Equal(2000L, item["quantity_milli"]);

        Assert.Equal(3, snapshot.TotalRows);
    }

    [Fact]
    public async Task UploadWithArchive_CallsArchiveBeforeReplace()
    {
        var (service, handler) = CreateService(
            (ArchiveRpcPath, HttpStatusCode.OK, ArchivedBody(59)),
            (ReplaceRpcPath, HttpStatusCode.OK, """{"orders":2}"""));
        using var _ = service;

        await service.UploadWithArchiveAsync(SnapshotWithTwoOrders(), Session);

        Assert.Equal(
            new[] { "/rest/v1/rpc/archive_snapshot", "/rest/v1/rpc/replace_snapshot" },
            handler.Requests.Select(request => new Uri(request.Url).AbsolutePath));
    }

    [Fact]
    public async Task UploadWithArchive_ArchiveFails_DoesNotReplace()
    {
        var (service, handler) = CreateService(
            (ArchiveRpcPath, HttpStatusCode.InternalServerError, """{"message":"canceling statement due to timeout"}"""),
            (ReplaceRpcPath, HttpStatusCode.OK, """{"orders":2}"""));
        using var _ = service;

        var error = await Assert.ThrowsAsync<SyncException>(
            () => service.UploadWithArchiveAsync(SnapshotWithTwoOrders(), Session));

        Assert.Equal(
            "Не удалось сохранить копию текущего облака. Выгрузка отменена, данные в облаке не изменены.",
            error.Message);
        Assert.Equal("snapshot_archive", error.Table);

        // Причина не теряется: интерфейс и журнал показывают ответ сервера.
        var cause = Assert.IsType<SyncException>(error.InnerException);
        Assert.Contains("canceling statement due to timeout", cause.Message);

        // Замена не началась: в облаке остались прежние строки.
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/rest/v1/rpc/archive_snapshot", new Uri(request.Url).AbsolutePath);
    }

    [Fact]
    public async Task UploadWithArchive_ArchiveSkipped_StillReplaces()
    {
        var (service, handler) = CreateService(
            (ArchiveRpcPath, HttpStatusCode.OK, """{"archived":false,"reason":"empty"}"""),
            (ReplaceRpcPath, HttpStatusCode.OK, """{"orders":2}"""));
        using var _ = service;

        var result = await service.UploadWithArchiveAsync(SnapshotWithTwoOrders(), Session);

        Assert.False(result.Archive.Archived);
        Assert.Equal("empty", result.Archive.Reason);
        Assert.Equal(2, result.Upload.TotalRows);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("/rest/v1/rpc/replace_snapshot", new Uri(handler.Requests[1].Url).AbsolutePath);
    }

    [Fact]
    public async Task UploadWithArchive_ReturnsBothResults()
    {
        var archiveId = Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444");
        var (service, _) = CreateService(
            (ArchiveRpcPath, HttpStatusCode.OK, ArchivedBody(59)),
            (ReplaceRpcPath, HttpStatusCode.OK, """{"orders":2}"""));
        using var __ = service;

        var result = await service.UploadWithArchiveAsync(SnapshotWithTwoOrders(), Session);

        Assert.Equal(archiveId, result.Archive.Id);
        Assert.Equal(59, result.Archive.TotalRows);
        Assert.Equal(2, result.Upload.TotalRows);
        Assert.Equal(2, result.Upload.RowCounts[SyncTables.Orders]);
    }

    private static string ArchivedBody(int totalRows) =>
        $$"""
            {"archived":true,"id":"aaaaaaaa-1111-2222-3333-444444444444",
             "taken_at":"2026-11-18T09:00:00.000000+00:00","schema_version":15,
             "total_rows":{{totalRows}}}
            """;

    /// <summary>Снапшот с двумя заказами: замена должна пройти не пустой.</summary>
    private static SyncSnapshot SnapshotWithTwoOrders()
    {
        static SyncOrder Order(int number, Guid id) => new()
        {
            Id = id,
            Number = number,
            FirstName = "Анна",
            Phone = "+7 900 111-22-33",
            DueDate = new DateOnly(2026, 11, 19),
            Status = "New",
            TotalPriceKopecks = 150000,
            PaidAmountKopecks = 0,
            DiscountKopecks = 0,
            CreatedAt = new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero),
        };

        return new SyncSnapshot
        {
            SchemaVersion = 15,
            TakenAt = new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.FromHours(3)),
            IngredientCategories = Array.Empty<SyncIngredientCategory>(),
            RecipeCategories = Array.Empty<SyncRecipeCategory>(),
            PackagingCategories = Array.Empty<SyncPackagingCategory>(),
            Ingredients = Array.Empty<SyncIngredient>(),
            Purchases = Array.Empty<SyncPurchase>(),
            Recipes = Array.Empty<SyncRecipe>(),
            RecipeIngredients = Array.Empty<SyncRecipeIngredient>(),
            Customers = Array.Empty<SyncCustomer>(),
            Orders = new[]
            {
                Order(7, Guid.Parse("cccccccc-0000-0000-0000-000000000001")),
                Order(8, Guid.Parse("cccccccc-0000-0000-0000-000000000002")),
            },
            OrderItems = Array.Empty<SyncOrderItem>(),
            PackagingItems = Array.Empty<SyncPackagingItem>(),
            OrderPackaging = Array.Empty<SyncOrderPackaging>(),
        };
    }

    private static (SyncService Service, RouteHandler Handler) CreateService(
        params (string Path, HttpStatusCode Status, string Body)[] routes)
    {
        var handler = new RouteHandler();
        foreach (var route in routes)
        {
            handler.Routes[route.Path] = (route.Status, route.Body);
        }

        return (new SyncService(Options, new HttpClient(handler)), handler);
    }

    private static (SyncService Service, RouteHandler Handler) CreateService(
        string path,
        string body,
        HttpStatusCode status = HttpStatusCode.OK) =>
        CreateService((path, status, body));

    private sealed record CapturedRequest(
        HttpMethod Method,
        string Url,
        string? ApiKey,
        string? Authorization,
        string Body);

    /// <summary>
    /// Отвечает по пути запроса: одному методу нужно разное поведение для RPC,
    /// архива и <c>sync_state</c>, поэтому один ответ на всё не подходит.
    /// </summary>
    private sealed class RouteHandler : HttpMessageHandler
    {
        public Dictionary<string, (HttpStatusCode Status, string Body)> Routes { get; } =
            new(StringComparer.Ordinal);

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

            if (!Routes.TryGetValue(request.RequestUri.AbsolutePath, out var route))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent(
                        """{"message":"маршрут не настроен"}""",
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            return new HttpResponseMessage(route.Status)
            {
                Content = new StringContent(route.Body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
