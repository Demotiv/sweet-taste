using System.Globalization;
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
/// Обменивается снапшотами с облаком через PostgREST: выгрузка и чтение
/// бизнес-таблиц, состояние облака и история снапшотов.
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

    /// <summary>Сколько строк просить за один запрос при скачивании таблицы.</summary>
    /// <remarks>
    /// Supabase по умолчанию отдаёт не больше 1000 строк на запрос (max rows),
    /// и лишнее молча отрезает. Поэтому таблица читается страницами, пока сервер
    /// не вернёт пустую страницу: так чтение верно и при меньшем лимите сервера.
    /// </remarks>
    public const int DownloadPageSize = 1000;

    /// <summary>
    /// Читает снапшот пользователя из облака. Требует действующей сессии.
    /// </summary>
    /// <remarks>
    /// Сначала читается <c>sync_state</c>, затем таблицы по одной в порядке
    /// <see cref="SnapshotRows.LoadOrder"/>, каждая страницами по
    /// <see cref="DownloadPageSize"/>. RLS отдаёт только строки текущего
    /// пользователя, поэтому фильтр в запросе не нужен. Пустое облако — это
    /// снапшот без строк, а не ошибка.
    /// <para>
    /// Число строк каждой таблицы сверяется с <c>sync_state.row_counts</c>: неполный
    /// снапшот, заменивший бы локальную базу, хуже ошибки. Расхождение бывает и
    /// когда другой компьютер выгружает данные прямо во время чтения — тогда
    /// помогает повтор. Метка времени снапшота берётся из <c>sync_state</c>: по ней
    /// выгрузка при закрытии узнаёт, с какой версией облака этот компьютер в согласии.
    /// </para>
    /// </remarks>
    /// <exception cref="SyncException">
    /// Облако вернуло не столько строк, сколько записано в <c>sync_state</c>.
    /// </exception>
    public async Task<SyncSnapshot> DownloadAsync(
        AuthSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var state = await ReadCloudStateAsync(session, cancellationToken);

        var tables = new Dictionary<string, IReadOnlyList<Dictionary<string, JsonElement>>>();
        foreach (var table in SnapshotRows.LoadOrder)
        {
            tables[table] = await SelectAllPagesAsync(table, session, cancellationToken);
        }

        if (state is not null)
        {
            foreach (var table in SnapshotRows.LoadOrder)
            {
                if (state.RowCounts.TryGetValue(table, out var expected) && tables[table].Count != expected)
                {
                    throw new SyncException(
                        $"Облако вернуло не все строки таблицы «{table}»: {tables[table].Count} из {expected}. " +
                        "Данные на этом компьютере не изменены. Повторите загрузку через минуту.",
                        table);
                }
            }

            return SnapshotReader.Read(tables, state.TakenAt);
        }

        return SnapshotReader.Read(tables, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Снимает копию текущего облачного снапшота в историю.
    /// </summary>
    /// <remarks>
    /// Вызывается из <see cref="UploadWithArchiveAsync"/> перед заменой: она стирает строки
    /// пользователя, поэтому архив берётся раньше. Отказы сервера
    /// (<c>empty</c>, <c>duplicate</c>, <c>no_state</c>) приходят как
    /// <see cref="ArchiveResult"/> с <c>Archived = false</c> — это не ошибка,
    /// и выгрузку можно продолжать.
    /// </remarks>
    public async Task<ArchiveResult> ArchiveAsync(
        AuthSession session,
        int keep = 10,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var request = CreateRequest(HttpMethod.Post, $"{_options.RestUrl}/rpc/archive_snapshot", session);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { p_keep = keep }),
            Encoding.UTF8,
            "application/json");

        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw CreateError(response.StatusCode, body, "archive_snapshot");
        }

        return ReadArchiveResult(body);
    }

    /// <summary>
    /// Снимает копию текущего облака в историю и только потом заменяет его
    /// новым снапшотом.
    /// </summary>
    /// <remarks>
    /// Обе операции намеренно не объединены в одну транзакцию. Архив обязан
    /// переживать неудачную замену: если <c>archive_snapshot</c> закоммичен,
    /// а <c>replace_snapshot</c> откатился, облако остаётся прежним и его копия
    /// уже лежит в истории. Обратный порядок такого не даёт — замена стёрла бы
    /// единственную копию. Замена не начинается без архива: сбой архивации
    /// отменяет выгрузку, и в облаке ничего не меняется. Повтор после обрыва
    /// самолечится: неизменённое облако сервер вернёт как <c>duplicate</c>,
    /// и замена пройдёт.
    /// </remarks>
    /// <exception cref="SyncException">
    /// Копию сохранить не удалось — замена не выполнялась.
    /// </exception>
    public async Task<UploadWithArchiveResult> UploadWithArchiveAsync(
        SyncSnapshot snapshot,
        AuthSession session,
        int keep = 10,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(session);

        ArchiveResult archive;
        try
        {
            archive = await ArchiveAsync(session, keep, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new SyncException(
                "Не удалось сохранить копию текущего облака. Выгрузка отменена, данные в облаке не изменены.",
                "snapshot_archive",
                exception);
        }

        // empty, duplicate и no_state — ответ сервера, а не ошибка: заменять
        // всё равно есть что, поэтому выгрузка продолжается.
        var upload = await UploadAtomicAsync(snapshot, session, cancellationToken);

        return new UploadWithArchiveResult
        {
            Archive = archive,
            Upload = upload,
        };
    }

    /// <summary>Список истории снапшотов, свежие сверху.</summary>
    /// <remarks>
    /// <c>tables</c> не запрашивается: список нужен только для выбора версии,
    /// а каждый архив — сотни килобайт JSON.
    /// </remarks>
    public async Task<IReadOnlyList<SnapshotArchiveInfo>> ListArchivesAsync(
        AuthSession session,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var rows = await SelectAsync(
            $"snapshot_archive?select=id,taken_at,schema_version,total_rows,archived_at&order=archived_at.desc&limit={limit}",
            session,
            cancellationToken);

        return rows.Select(SnapshotReader.ParseArchiveInfo).ToList();
    }

    /// <summary>Читает снапшот из истории облака.</summary>
    /// <remarks>
    /// Версия схемы берётся из самого архива: копию могли сделать прошлой
    /// версией приложения, и подменять её текущей нельзя.
    /// </remarks>
    public async Task<SyncSnapshot> DownloadArchiveAsync(
        Guid archiveId,
        AuthSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var rows = await SelectAsync(
            $"snapshot_archive?select=schema_version,taken_at,tables&id=eq.{archiveId:D}&limit=1",
            session,
            cancellationToken);

        // Пустой ответ — архив обрезан или удалён с другого устройства;
        // список в интерфейсе устарел.
        if (rows.Count == 0)
        {
            throw new SyncException("Архивный снапшот не найден. Обновите список.", "snapshot_archive");
        }

        var row = rows[0];
        var takenAt = DateTimeOffset.Parse(row["taken_at"].GetString()!, CultureInfo.InvariantCulture);
        var schemaVersion = (int)row["schema_version"].GetInt64();

        return SnapshotReader.Read(
            SnapshotReader.ParseTables(row["tables"]),
            takenAt,
            schemaVersion);
    }

    /// <summary>Состояние облака из <c>sync_state</c> или <c>null</c>, если облако пустое.</summary>
    public async Task<CloudState?> ReadCloudStateAsync(
        AuthSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var rows = await SelectAsync(
            "sync_state?select=schema_version,last_snapshot_at,row_counts&limit=1",
            session,
            cancellationToken);

        return rows.Count == 0 ? null : SnapshotReader.ParseCloudState(rows[0]);
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    /// <summary>Читает строки таблицы по полному пути с query-строкой PostgREST.</summary>
    private async Task<IReadOnlyList<Dictionary<string, JsonElement>>> SelectAsync(
        string path,
        AuthSession session,
        CancellationToken cancellationToken)
    {
        var table = path.Split('?')[0];
        var request = CreateRequest(HttpMethod.Get, $"{_options.RestUrl}/{path}", session);

        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw CreateError(response.StatusCode, body, table, "прочитать");
        }

        return SnapshotReader.ParseRows(body, table);
    }

    /// <summary>Читает таблицу целиком страницами, пока сервер не вернёт пустую.</summary>
    private async Task<IReadOnlyList<Dictionary<string, JsonElement>>> SelectAllPagesAsync(
        string table,
        AuthSession session,
        CancellationToken cancellationToken)
    {
        // Без устойчивого порядка страницы могут пересекаться или пропускать строки.
        var order = table == SyncTables.RecipeIngredients ? "recipe_id,ingredient_id" : "id";
        var rows = new List<Dictionary<string, JsonElement>>();

        while (true)
        {
            var page = await SelectAsync(
                $"{table}?select=*&order={order}&limit={DownloadPageSize}&offset={rows.Count}",
                session,
                cancellationToken);

            if (page.Count == 0)
            {
                return rows;
            }

            rows.AddRange(page);
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
            return new SyncException(
                action == "прочитать"
                    ? "Сервер отклонил чтение. Проверьте политики доступа."
                    : "Сервер отклонил запись. Проверьте политики доступа.",
                table);
        }

        var prefix = action is null
            ? $"Не удалось выгрузить снапшот ({table})"
            : $"Не удалось {action} таблицу «{table}»";

        return new SyncException(
            detail is null ? prefix + ". Проверьте интернет." : prefix + ": " + detail,
            table);
    }

    private static ArchiveResult ReadArchiveResult(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new SyncException("Сервер вернул не результат архивации.");
            }

            if (!root.TryGetProperty("archived", out var archived) || archived.ValueKind != JsonValueKind.True)
            {
                return new ArchiveResult
                {
                    Archived = false,
                    Reason = root.TryGetProperty("reason", out var reason) ? reason.GetString() : null,
                };
            }

            return new ArchiveResult
            {
                Archived = true,
                Id = root.TryGetProperty("id", out var id) ? id.GetGuid() : null,
                TakenAt = root.TryGetProperty("taken_at", out var takenAt) ? takenAt.GetDateTimeOffset() : null,
                TotalRows = root.TryGetProperty("total_rows", out var totalRows) ? totalRows.GetInt32() : 0,
            };
        }
        catch (JsonException exception)
        {
            throw new SyncException(
                "Не удалось разобрать ответ сервера (archive_snapshot).",
                "archive_snapshot",
                exception);
        }
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
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

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
