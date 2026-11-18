namespace SweetTaste.Core.Sync;

/// <summary>
/// Строка истории снапшотов в облаке — без самого архива.
/// </summary>
/// <remarks>
/// Список нужен, чтобы выбрать версию, поэтому <c>tables</c> здесь не запрашивается:
/// один архив — это сотни килобайт JSON, и тянуть двадцать таких на каждое открытие
/// диалога незачем. Полный снапшот читается по одному id.
/// </remarks>
public sealed record SnapshotArchiveInfo
{
    public required Guid Id { get; init; }

    public required DateTimeOffset TakenAt { get; init; }

    public required int SchemaVersion { get; init; }

    public required int TotalRows { get; init; }

    public required DateTimeOffset ArchivedAt { get; init; }
}

/// <summary>Итог вызова <c>archive_snapshot</c>.</summary>
/// <remarks>
/// Сервер отказывается архивировать по трём безобидным причинам: облако ещё
/// не синхронизировалось (<c>no_state</c>), в нём нет строк (<c>empty</c>) или
/// текущее состояние уже лежит в архиве (<c>duplicate</c>). Это ответ, а не
/// ошибка: замену снапшота всё равно можно продолжать.
/// </remarks>
public sealed record ArchiveResult
{
    public required bool Archived { get; init; }

    /// <summary>Причина отказа сервера: <c>empty</c>, <c>duplicate</c> или <c>no_state</c>.</summary>
    public string? Reason { get; init; }

    public Guid? Id { get; init; }

    public DateTimeOffset? TakenAt { get; init; }

    public int TotalRows { get; init; }
}

/// <summary>Итог выгрузки в порядке «архив → замена».</summary>
/// <remarks>
/// Вызывающему нужны оба ответа: по <see cref="Archive"/> интерфейс показывает,
/// сохранилась ли копия прежнего облака и почему нет (<c>empty</c>,
/// <c>duplicate</c>, <c>no_state</c> — это ответ сервера, а не сбой),
/// а по <see cref="Upload"/> сверяет число строк после замены. Одного
/// <see cref="SyncResult"/> мало: пропущенный архив прошёл бы незаметно.
/// </remarks>
public sealed record UploadWithArchiveResult
{
    public required ArchiveResult Archive { get; init; }

    public required SyncResult Upload { get; init; }
}

/// <summary>Слепок <c>sync_state</c>: что сейчас лежит в облаке.</summary>
/// <remarks>
/// Нужен интерфейсу, чтобы показать, что именно заменит выгрузка. Счётчики
/// приходят из JSON и не зависят от схемы: состояние видно без чтения самих
/// таблиц, а <see cref="TotalRows"/> считается по ним же.
/// </remarks>
public sealed record CloudState
{
    public required DateTimeOffset TakenAt { get; init; }

    public required int SchemaVersion { get; init; }

    public required int TotalRows { get; init; }

    public required IReadOnlyDictionary<string, int> RowCounts { get; init; }
}
