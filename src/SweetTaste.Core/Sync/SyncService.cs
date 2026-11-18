using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SweetTaste.Core.Sync;

/// <summary>Ошибка обмена с облаком с понятным текстом для интерфейса.</summary>
public sealed class SyncException : Exception
{
    public SyncException(string message, string? table = null, Exception? inner = null)
        : base(message, inner)
    {
        Table = table;
    }

    /// <summary>Таблица, на которой упала загрузка, если ошибка привязана к ней.</summary>
    public string? Table { get; }
}

/// <summary>Итог одной выгрузки снапшота в облако.</summary>
public sealed record SyncResult
{
    public required DateTimeOffset TakenAt { get; init; }

    public required int TotalRows { get; init; }

    public required IReadOnlyDictionary<string, int> RowCounts { get; init; }
}

/// <summary>
/// Загружает снапшот в облако через PostgREST: очищает таблицы пользователя
/// и вставляет строки снапшота.
/// </summary>
/// <remarks>
/// Локальная база — источник истины, облако — реплика, поэтому каждая выгрузка
/// начинается с удаления всех строк пользователя. Удаление идёт в обратном
/// порядке загрузки: сначала зависимые таблицы, потом справочники, иначе
/// внешние ключи не дадут удалить родителя.
/// </remarks>
public sealed class SyncService : IDisposable
{
    private readonly HttpClient _http;
    private readonly SupabaseOptions _options;
    private readonly bool _ownsHttp;

    public SyncService(SupabaseOptions options, HttpClient? http = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    /// <summary>Выгружает снапшот целиком. Требует действующей сессии.</summary>
    public async Task<SyncResult> UploadAsync(
        SyncSnapshot snapshot,
        AuthSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(session);

        await DeleteAllAsync(session, cancellationToken);

        foreach (var table in SnapshotRows.LoadOrder)
        {
            var rows = SnapshotRows.For(snapshot, table);
            if (rows.Count == 0)
            {
                continue;
            }

            await InsertAsync(table, rows, session, cancellationToken);
        }

        await SaveStateAsync(snapshot, session, cancellationToken);

        return new SyncResult
        {
            TakenAt = snapshot.TakenAt,
            TotalRows = snapshot.TotalRows,
            RowCounts = snapshot.RowCounts,
        };
    }

    /// <summary>
    /// Заменяет снапшот одной серверной функцией <c>replace_snapshot</c>.
    /// </summary>
    /// <remarks>
    /// В отличие от <see cref="UploadAsync"/>, очистка и вставка идут в одной
    /// транзакции на сервере: обрыв сети не оставит облако пустым.
    /// </remarks>
    public async Task<SyncResult> UploadAtomicAsync(
        SyncSnapshot snapshot,
        AuthSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(session);

        var tables = new Dictionary<string, IReadOnlyList<Dictionary<string, object?>>>();
        foreach (var table in SnapshotRows.LoadOrder)
        {
            tables[table] = SnapshotRows.For(snapshot, table);
        }

        var payload = new Dictionary<string, object?>
        {
            ["p_schema_version"] = snapshot.SchemaVersion,
            ["p_taken_at"] = Timestamp(snapshot.TakenAt),
            ["p_tables"] = tables,
        };

        var request = CreateRequest(HttpMethod.Post, $"{_options.RestUrl}/rpc/replace_snapshot", session);
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw CreateError(response.StatusCode, body, "replace_snapshot");
        }

        return new SyncResult
        {
            TakenAt = snapshot.TakenAt,
            TotalRows = snapshot.TotalRows,
            RowCounts = ReadCounts(body) ?? snapshot.RowCounts,
        };
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    private async Task DeleteAllAsync(AuthSession session, CancellationToken cancellationToken)
    {
        foreach (var table in SnapshotRows.LoadOrder.Reverse())
        {
            // Фильтр по id обязателен: PostgREST не удаляет без условия.
            // RLS всё равно ограничит удаление строками текущего пользователя.
            var request = CreateRequest(
                HttpMethod.Delete,
                $"{_options.RestUrl}/{table}?id=not.is.null",
                session);
            request.Headers.TryAddWithoutValidation("Prefer", "return=minimal");

            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw await CreateErrorAsync(response, table, "очистить", cancellationToken);
            }
        }
    }

    private async Task InsertAsync(
        string table,
        IReadOnlyList<Dictionary<string, object?>> rows,
        AuthSession session,
        CancellationToken cancellationToken)
    {
        var request = CreateRequest(HttpMethod.Post, $"{_options.RestUrl}/{table}", session);
        request.Headers.TryAddWithoutValidation("Prefer", "return=minimal");
        request.Content = new StringContent(
            SnapshotRows.ToJson(rows),
            Encoding.UTF8,
            "application/json");

        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateErrorAsync(response, table, "загрузить", cancellationToken);
        }
    }

    private async Task SaveStateAsync(
        SyncSnapshot snapshot,
        AuthSession session,
        CancellationToken cancellationToken)
    {
        var row = new Dictionary<string, object?>
        {
            ["user_id"] = session.UserId.ToString(),
            ["schema_version"] = snapshot.SchemaVersion,
            ["last_snapshot_at"] = snapshot.TakenAt.ToUniversalTime()
                .ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", System.Globalization.CultureInfo.InvariantCulture),
            ["row_counts"] = snapshot.RowCounts,
        };

        var request = CreateRequest(HttpMethod.Post, $"{_options.RestUrl}/sync_state", session);
        request.Headers.TryAddWithoutValidation("Prefer", "resolution=merge-duplicates,return=minimal");
        request.Content = new StringContent(
            JsonSerializer.Serialize(new[] { row }),
            Encoding.UTF8,
            "application/json");

        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateErrorAsync(response, "sync_state", "сохранить состояние", cancellationToken);
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string url, AuthSession session)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("apikey", _options.PublishableKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        return request;
    }

    private static async Task<SyncException> CreateErrorAsync(
        HttpResponseMessage response,
        string table,
        string action,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return CreateError(response.StatusCode, body, table, action);
    }

    private static SyncException CreateError(
        HttpStatusCode status,
        string body,
        string table,
        string? action = null)
    {
        var detail = ReadDetail(body);

        if (status == HttpStatusCode.Unauthorized)
        {
            return new SyncException("Сессия истекла. Войдите заново.", table);
        }

        if (status == HttpStatusCode.Forbidden)
        {
            return new SyncException("Сервер отклонил запись. Проверьте политики доступа.", table);
        }

        var prefix = action is null
            ? $"Не удалось выгрузить снапшот ({table})"
            : $"Не удалось {action} таблицу «{table}»";

        return new SyncException(
            detail is null ? prefix + ". Проверьте интернет." : prefix + ": " + detail,
            table);
    }

    private static IReadOnlyDictionary<string, int>? ReadCounts(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var counts = new Dictionary<string, int>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Number &&
                    property.Value.TryGetInt32(out var value))
                {
                    counts[property.Name] = value;
                }
            }

            return counts.Count == 0 ? null : counts;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime()
            .ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static string? ReadDetail(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("message", out var message))
            {
                return message.GetString();
            }

            if (root.TryGetProperty("hint", out var hint))
            {
                return hint.GetString();
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
