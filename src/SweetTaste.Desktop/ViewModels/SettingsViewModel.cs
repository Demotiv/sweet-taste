using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SweetTaste.Core.Domain;
using SweetTaste.Core.Services;
using SweetTaste.Core.Sync;

namespace SweetTaste.Desktop.ViewModels;

/// <summary>Настройки: синхронизация, тема, резервные копии, значения по умолчанию и очистка данных.</summary>
public sealed partial class SettingsViewModel : ViewModelBase, IFormPage
{
    private readonly SettingsService _settings;
    private readonly OrderService _orders;
    private readonly SupabaseAuthClient _auth;
    private readonly ISessionStore _sessions;
    private readonly SyncService _sync;
    private Action? _confirmAction;
    private SyncSnapshot? _pendingUpload;

    /// <summary>Сколько версий прежнего облака держит архив: то же число уходит в <c>archive_snapshot</c>.</summary>
    private const int ArchiveKeep = 10;

    /// <summary>Сообщает главному окну, что заказы изменились (напоминание о выдачах).</summary>
    public event Action? OrdersChanged;

    /// <summary>Просит главное окно проверить обновления вручную.</summary>
    public event Action? CheckUpdatesRequested;

    public string VersionText => AppInfo.VersionLine;

    public SettingsViewModel(
        SettingsService settings,
        OrderService orders,
        SupabaseAuthClient auth,
        ISessionStore sessions,
        SyncService sync)
    {
        _settings = settings;
        _orders = orders;
        _auth = auth;
        _sessions = sessions;
        _sync = sync;

        _selectedTheme = ThemeName(settings.Theme);
        _selectedRetention = settings.BackupRetentionDays;
        _markup = settings.DefaultMarkupPercent;
        _recipeUnit = settings.DefaultRecipeUnit;
        _selectedScale = settings.UiScalePercent;
        _uploadOnExit = settings.UploadOnExit;
        _databasePath = settings.DatabasePath;
        ReloadOrderCounts();
        RestoreSession();
    }

    public IReadOnlyList<string> ThemeOptions { get; } = new[] { "Светлая", "Тёмная", "Как в системе" };

    public IReadOnlyList<int> RetentionOptions { get; } = new[] { 7, 14, 30, 90 };

    public IReadOnlyList<int> ScaleOptions { get; } = new[] { 90, 100, 110, 125 };

    public IReadOnlyList<string> UnitsList => RecipeUnits.Allowed;

    [ObservableProperty]
    private string _selectedTheme;

    [ObservableProperty]
    private int _selectedRetention;

    [ObservableProperty]
    private decimal? _markup;

    [ObservableProperty]
    private string _recipeUnit;

    [ObservableProperty]
    private int _selectedScale;

    [ObservableProperty]
    private string _databasePath;

    [ObservableProperty]
    private string? _backupMessage;

    [ObservableProperty]
    private bool _hasBackupMessage;

    [ObservableProperty]
    private string? _backupError;

    [ObservableProperty]
    private string _ordersSummary = string.Empty;

    [ObservableProperty]
    private bool _hasArchivedOrders;

    [ObservableProperty]
    private bool _hasAnyOrders;

    [ObservableProperty]
    private bool _hasCurrentOrders;

    [ObservableProperty]
    private string? _dangerMessage;

    [ObservableProperty]
    private bool _hasDangerMessage;

    [ObservableProperty]
    private string? _dangerError;

    [ObservableProperty]
    private bool _isConfirmOpen;

    [ObservableProperty]
    private string _confirmText = string.Empty;

    [ObservableProperty]
    private string _confirmButtonText = "Удалить";

    [ObservableProperty]
    private bool _isRestoreCompleted;

    [ObservableProperty]
    private string _syncEmail = string.Empty;

    [ObservableProperty]
    private string _syncPassword = string.Empty;

    [ObservableProperty]
    private bool _isSignedIn;

    [ObservableProperty]
    private string _signedInEmail = string.Empty;

    [ObservableProperty]
    private bool _isSyncBusy;

    [ObservableProperty]
    private string? _syncError;

    [ObservableProperty]
    private string? _syncMessage;

    [ObservableProperty]
    private bool _hasSyncMessage;

    [ObservableProperty]
    private bool _isSyncConfirmOpen;

    [ObservableProperty]
    private string _syncConfirmText = string.Empty;

    [ObservableProperty]
    private bool _isUploadConfirmOpen;

    [ObservableProperty]
    private string _uploadConfirmText = string.Empty;

    [ObservableProperty]
    private bool _hasUploadWarning;

    [ObservableProperty]
    private string _uploadConfirmButtonText = "Выгрузить";

    [ObservableProperty]
    private bool _isSyncRestartNeeded;

    [ObservableProperty]
    private string _localDataMoment = string.Empty;

    [ObservableProperty]
    private string _cloudDataMoment = string.Empty;

    [ObservableProperty]
    private bool _hasUnsyncedChanges;

    [ObservableProperty]
    private bool _hasDataMoments;

    [ObservableProperty]
    private bool _isCloudMomentStale;

    [ObservableProperty]
    private bool _uploadOnExit;

    [ObservableProperty]
    private bool _isArchiveOpen;

    [ObservableProperty]
    private IReadOnlyList<ArchiveOption> _archiveOptions = Array.Empty<ArchiveOption>();

    [ObservableProperty]
    private ArchiveOption? _selectedArchive;

    [ObservableProperty]
    private string? _archiveError;

    [ObservableProperty]
    private bool _isArchiveBusy;

    public string BackupFolder => _settings.BackupDirectory;

    public bool IsFormOpen =>
        IsConfirmOpen || IsSyncConfirmOpen || IsUploadConfirmOpen || IsArchiveOpen;

    public void CancelForm()
    {
        if (IsConfirmOpen)
        {
            IsConfirmOpen = false;
            _confirmAction = null;
        }

        if (IsSyncConfirmOpen)
        {
            IsSyncConfirmOpen = false;
        }

        if (IsUploadConfirmOpen)
        {
            IsUploadConfirmOpen = false;
            _pendingUpload = null;
        }

        CancelArchive();
    }

    partial void OnSelectedThemeChanged(string value)
    {
        var theme = value switch
        {
            "Тёмная" => SettingsService.ThemeDark,
            "Как в системе" => SettingsService.ThemeSystem,
            _ => SettingsService.ThemeLight,
        };

        _settings.Theme = theme;
        App.ApplyTheme(theme);
    }

    partial void OnUploadOnExitChanged(bool value) => _settings.UploadOnExit = value;

    partial void OnSelectedRetentionChanged(int value)
    {
        try
        {
            _settings.BackupRetentionDays = value;
            BackupError = null;
        }
        catch (ValidationException ex)
        {
            BackupError = ex.Message;
        }
    }

    partial void OnMarkupChanged(decimal? value)
    {
        _settings.DefaultMarkupPercent = (int)Math.Round(value.GetValueOrDefault());
    }

    partial void OnRecipeUnitChanged(string value)
    {
        if (RecipeUnits.IsAllowed(value))
        {
            _settings.DefaultRecipeUnit = value;
        }
    }

    partial void OnSelectedScaleChanged(int value)
    {
        try
        {
            _settings.UiScalePercent = value;
            App.ApplyUiScale(value);
        }
        catch (ValidationException ex)
        {
            BackupError = ex.Message;
        }
    }

    [RelayCommand]
    private void CreateBackup()
    {
        var path = _settings.CreateBackupNow();

        if (path is null)
        {
            HasBackupMessage = false;
            BackupError = "Не удалось создать бэкап: база ещё не создана или файл занят.";
            return;
        }

        BackupError = null;
        BackupMessage = "Бэкап создан: " + Path.GetFileName(path);
        HasBackupMessage = true;
    }

    /// <summary>Вызывается после выбора файла базы: показывает подтверждение замены.</summary>
    public void RequestRestore(string sourcePath)
    {
        _confirmAction = () =>
        {
            try
            {
                var backup = _settings.RestoreDatabase(sourcePath);
                BackupError = null;
                BackupMessage = backup is null
                    ? "Данные восстановлены. Перезапустите приложение, чтобы увидеть их."
                    : $"Данные восстановлены. Старая база сохранена как {Path.GetFileName(backup)}. " +
                      "Перезапустите приложение, чтобы увидеть новые данные.";
                HasBackupMessage = true;
                IsRestoreCompleted = true;
            }
            catch (ValidationException ex)
            {
                BackupError = ex.Message;
                HasBackupMessage = false;
            }
            catch (Exception ex)
            {
                BackupError = "Не удалось восстановить базу: " + ex.Message;
                HasBackupMessage = false;
            }
        };

        ConfirmText = "Заменить текущую базу выбранным файлом? Текущие данные будут сохранены " +
                      "в резервную копию, но в приложении появятся данные из файла. " +
                      "После восстановления приложение нужно перезапустить.";
        ConfirmButtonText = "Восстановить";
        IsConfirmOpen = true;
    }

    public void SetRestoreError(string message)
    {
        BackupMessage = null;
        HasBackupMessage = false;
        BackupError = message;
    }

    [RelayCommand]
    private void RestartApplication()
    {
        var processPath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            BackupError = "Не удалось определить путь к приложению. Закройте и запустите его вручную.";
            return;
        }

        Process.Start(new ProcessStartInfo(processPath) { UseShellExecute = true });

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    [RelayCommand]
    private void CheckUpdates() => CheckUpdatesRequested?.Invoke();

    /// <summary>Вход по email и паролю. Пароль после успешного входа стирается.</summary>
    [RelayCommand]
    private async Task SignInAsync()
    {
        if (IsSyncBusy)
        {
            return;
        }

        var email = SyncEmail.Trim();
        if (email.Length == 0 || SyncPassword.Length == 0)
        {
            SyncError = "Введите email и пароль.";
            HasSyncMessage = false;
            return;
        }

        IsSyncBusy = true;
        SyncError = null;
        HasSyncMessage = false;

        try
        {
            var session = await _auth.SignInAsync(email, SyncPassword);
            _sessions.Save(session);

            SyncPassword = string.Empty;
            SignedInEmail = session.Email;
            IsSignedIn = true;
            SyncMessage = "Вход выполнен.";
            HasSyncMessage = true;
            await RefreshDataMomentsAsync();
        }
        catch (AuthException ex)
        {
            SyncError = ex.Message;
        }
        catch (Exception ex)
        {
            SyncError = "Не удалось войти: " + ex.Message;
        }
        finally
        {
            IsSyncBusy = false;
        }
    }

    /// <summary>Выход: сессия удаляется с диска, локальные данные остаются.</summary>
    [RelayCommand]
    private void SignOut()
    {
        _sessions.Clear();
        IsSignedIn = false;
        SignedInEmail = string.Empty;
        SyncPassword = string.Empty;
        SyncError = null;
        SyncMessage = "Вы вышли из аккаунта. Локальные данные не тронуты.";
        HasSyncMessage = true;
        LocalDataMoment = string.Empty;
        CloudDataMoment = string.Empty;
        HasUnsyncedChanges = false;
        HasDataMoments = false;
        IsCloudMomentStale = false;
    }

    private void RestoreSession()
    {
        var session = _sessions.Load();
        if (session is null)
        {
            return;
        }

        SignedInEmail = session.Email;
        IsSignedIn = true;

        // Строки «на этом компьютере / в облаке» подтягиваются следом: облако
        // читается по сети, и открытие страницы не должно ждать ответа.
        _ = RefreshDataMomentsAsync();
    }

    /// <summary>
    /// Заполняет две строки карточки: когда данные менялись здесь и когда обновлялось
    /// облако. По разнице видно, есть ли правки, которые ещё не ушли в облако.
    /// </summary>
    private async Task RefreshDataMomentsAsync()
    {
        var local = _settings.DataChangedAt;
        var cloud = await ReadCloudMomentAsync();

        LocalDataMoment = local is null
            ? "На этом компьютере: ещё не менялись"
            : "На этом компьютере: " + CloudMoment(local.Value);
        CloudDataMoment = cloud is null
            ? "В облаке: нет данных"
            : "В облаке: " + CloudMoment(cloud.Value);

        // Секунда допуска: облако и триггеры пишут время в разные моменты,
        // и без запаса строка мигала бы сразу после выгрузки.
        HasUnsyncedChanges = local is not null && cloud is not null &&
                             local.Value > cloud.Value.AddSeconds(1);
        HasDataMoments = IsSignedIn;
    }

    /// <summary>Перечитывает время правки здесь и время выгрузки в облако по кнопке.</summary>
    [RelayCommand]
    private async Task RefreshCloudMomentAsync()
    {
        if (IsSyncBusy)
        {
            return;
        }

        IsSyncBusy = true;
        SyncError = null;
        HasSyncMessage = false;

        try
        {
            await RefreshDataMomentsAsync();
            IsCloudMomentStale = false;
        }
        catch (Exception ex)
        {
            SyncError = "Не удалось проверить облако: " + ex.Message;
        }
        finally
        {
            IsSyncBusy = false;
        }
    }

    /// <summary>Время последней выгрузки в облако или null, если облако пустое и недоступно.</summary>
    private async Task<DateTimeOffset?> ReadCloudMomentAsync()
    {
        var session = _sessions.Load();
        if (session is null)
        {
            return null;
        }

        try
        {
            var state = await _sync.ReadCloudStateAsync(session);
            IsCloudMomentStale = false;
            return state?.TakenAt;
        }
        catch (Exception)
        {
            // Без сети или с протухшим токеном строка облака остаётся «нет данных»:
            // это подсказка в карточке, а не действие, и ошибку показывать незачем.
            // Но «нет данных» и «не смогли прочитать» — разные вещи, поэтому
            // карточка помечает строку как непроверенную.
            IsCloudMomentStale = true;
            return null;
        }
    }

    /// <summary>Читает состояние облака и спрашивает подтверждение выгрузки — данные ещё не уходят.</summary>
    [RelayCommand]
    private async Task RequestUploadToCloudAsync()
    {
        if (IsSyncBusy)
        {
            return;
        }

        var session = await EnsureSessionAsync();
        if (session is null)
        {
            return;
        }

        IsSyncBusy = true;
        SyncError = null;
        HasSyncMessage = false;

        try
        {
            var snapshot = new SnapshotExporter(_settings.Database).Export(DateTimeOffset.UtcNow);
            var cloud = await _sync.ReadCloudStateAsync(session);

            _pendingUpload = snapshot;
            ShowUploadConfirm(snapshot, cloud);
            IsUploadConfirmOpen = true;
        }
        catch (SyncException ex)
        {
            SyncError = ex.Message;
        }
        catch (Exception ex)
        {
            SyncError = "Не удалось прочитать состояние облака: " + ex.Message;
        }
        finally
        {
            IsSyncBusy = false;
        }
    }

    /// <summary>Выгружает снапшот, показанный в подтверждении: сначала архив прежнего облака, затем замена.</summary>
    [RelayCommand]
    private async Task ConfirmUploadToCloudAsync()
    {
        IsUploadConfirmOpen = false;

        var snapshot = _pendingUpload;
        _pendingUpload = null;

        if (snapshot is null || IsSyncBusy)
        {
            return;
        }

        var session = await EnsureSessionAsync();
        if (session is null)
        {
            return;
        }

        IsSyncBusy = true;
        SyncError = null;
        HasSyncMessage = false;

        try
        {
            var result = await _sync.UploadWithArchiveAsync(snapshot, session, ArchiveKeep);
            var rows = result.Upload.TotalRows;
            var uploaded = $"Выгружено {rows} {Formats.Plural(rows, "строка", "строки", "строк")}.";
            var outcome = ArchiveOutcome(result.Archive);

            SyncMessage = outcome.Length == 0 ? uploaded : uploaded + " " + outcome;
            HasSyncMessage = true;
            await RefreshDataMomentsAsync();
        }
        catch (SyncException ex)
        {
            SyncError = ex.Message;
        }
        catch (Exception ex)
        {
            SyncError = "Не удалось выгрузить данные: " + ex.Message;
        }
        finally
        {
            IsSyncBusy = false;
        }
    }

    [RelayCommand]
    private void CancelUploadConfirm()
    {
        IsUploadConfirmOpen = false;
        _pendingUpload = null;
    }

    /// <summary>Собирает текст подтверждения: что сейчас в облаке и что уйдёт с этой машины.</summary>
    private void ShowUploadConfirm(SyncSnapshot snapshot, CloudState? cloud)
    {
        var lines = new List<string>();
        lines.Add(cloud is null
            ? "В облаке пока нет данных."
            : $"Сейчас в облаке: {cloud.TotalRows} " +
              $"{Formats.Plural(cloud.TotalRows, "строка", "строки", "строк")}, " +
              $"обновлено {CloudMoment(cloud.TakenAt)}.");

        var total = snapshot.TotalRows;
        var breakdown = DescribeRowCounts(snapshot.RowCounts);
        lines.Add($"С этой машины: {total} {Formats.Plural(total, "строка", "строки", "строк")}" +
                  (breakdown.Length == 0 ? "." : $" ({breakdown})."));

        // Пустая база затрёт непустое облако — про это отдельная красная строка в диалоге.
        var wipesCloud = total == 0 && cloud is { TotalRows: > 0 };
        if (wipesCloud)
        {
            lines.Add("Вы выгружаете пустую базу: прежняя версия останется в истории.");
        }
        else if (cloud is not null)
        {
            lines.Add($"Прежнее состояние облака сохранится в истории — последние {ArchiveKeep} версий.");
        }

        HasUploadWarning = wipesCloud;
        UploadConfirmButtonText = wipesCloud ? "Выгрузить пустую базу" : "Выгрузить";
        UploadConfirmText = string.Join("\n", lines);
    }

    /// <summary>Ненулевые таблицы в порядке <see cref="SyncSnapshot.RowCounts"/>: «заказы 17, позиции 20».</summary>
    private static string DescribeRowCounts(IReadOnlyDictionary<string, int> counts) =>
        string.Join(", ", UploadRowLabels
            .Where(row => counts.TryGetValue(row.Table, out var count) && count > 0)
            .Select(row => $"{row.Label} {counts[row.Table]}"));

    /// <summary>Что стало с прежним облаком; пустая строка — сервер не стал архивировать, сказать нечего.</summary>
    private static string ArchiveOutcome(ArchiveResult archive) => archive switch
    {
        { Archived: true } => "Прежнее состояние облака сохранено в истории.",
        { Reason: "duplicate" } => "Прежнее состояние уже было в истории.",
        { Reason: "empty" } => "Прежнее облако было пустым — архивировать нечего.",
        _ => string.Empty,
    };

    /// <summary>Момент выгрузки в облако: локальное время, дата и время через запятую.</summary>
    private static string CloudMoment(DateTimeOffset value) =>
        value.ToLocalTime().ToString("dd.MM.yyyy, HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Версия в списке истории: «18.11.2026, 22:38 · 59 строк · схема 15».</summary>
    private static string ArchiveText(SnapshotArchiveInfo info) =>
        $"{CloudMoment(info.ArchivedAt)} · {info.TotalRows} " +
        $"{Formats.Plural(info.TotalRows, "строка", "строки", "строк")} · схема {info.SchemaVersion}";

    /// <summary>Русские названия таблиц снапшота для подтверждения выгрузки.</summary>
    private static readonly (string Table, string Label)[] UploadRowLabels =
    {
        (SyncTables.IngredientCategories, "категории ингредиентов"),
        (SyncTables.RecipeCategories, "категории рецептов"),
        (SyncTables.PackagingCategories, "категории упаковки"),
        (SyncTables.Ingredients, "ингредиенты"),
        (SyncTables.Purchases, "закупки"),
        (SyncTables.Recipes, "рецепты"),
        (SyncTables.RecipeIngredients, "состав рецептов"),
        (SyncTables.Customers, "клиенты"),
        (SyncTables.Orders, "заказы"),
        (SyncTables.OrderItems, "позиции"),
        (SyncTables.PackagingItems, "упаковка"),
        (SyncTables.OrderPackaging, "упаковка в заказах"),
    };

    /// <summary>Спрашивает подтверждение перед заменой локальных данных данными из облака.</summary>
    [RelayCommand]
    private void RequestDownloadFromCloud()
    {
        if (IsSyncBusy)
        {
            return;
        }

        SyncConfirmText = "Заменить данные на этом компьютере данными из облака? " +
                          "Текущая база будет сохранена в резервную копию, но в приложении " +
                          "появятся данные из облака. После замены приложение нужно перезапустить.";
        IsSyncConfirmOpen = true;
    }

    [RelayCommand]
    private void CancelSyncConfirm() => IsSyncConfirmOpen = false;

    /// <summary>Заменяет локальные данные снапшотом из облака.</summary>
    [RelayCommand]
    private async Task ConfirmDownloadFromCloudAsync()
    {
        IsSyncConfirmOpen = false;

        if (IsSyncBusy)
        {
            return;
        }

        var session = await EnsureSessionAsync();
        if (session is null)
        {
            return;
        }

        IsSyncBusy = true;
        SyncError = null;
        HasSyncMessage = false;

        try
        {
            var snapshot = await _sync.DownloadAsync(session);

            if (snapshot.SchemaVersion != SnapshotReader.DatabaseSchemaVersion)
            {
                SyncError = $"Данные в облаке сохранены версией схемы {snapshot.SchemaVersion}, " +
                            $"а приложение понимает {SnapshotReader.DatabaseSchemaVersion}. " +
                            "Обновите приложение.";
                return;
            }

            var backup = _settings.CreateBackupNow();
            var imported = new SnapshotImporter(_settings.Database).Import(snapshot);

            SyncMessage = backup is null
                ? $"Данные из облака загружены: {imported} " +
                  $"{Formats.Plural(imported, "строка", "строки", "строк")}. Перезапустите приложение."
                : $"Данные из облака загружены: {imported} " +
                  $"{Formats.Plural(imported, "строка", "строки", "строк")}. " +
                  $"Прежняя база сохранена как {Path.GetFileName(backup)}. Перезапустите приложение.";
            HasSyncMessage = true;
            IsSyncRestartNeeded = true;
            ReloadOrderCounts();
            OrdersChanged?.Invoke();
            await RefreshDataMomentsAsync();
        }
        catch (SyncException ex)
        {
            SyncError = ex.Message;
        }
        catch (Exception ex)
        {
            SyncError = "Не удалось загрузить данные из облака: " + ex.Message;
        }
        finally
        {
            IsSyncBusy = false;
        }
    }

    /// <summary>Читает историю облака и открывает выбор версии — данные ещё не заменяются.</summary>
    [RelayCommand]
    private async Task OpenArchiveAsync()
    {
        if (IsSyncBusy || IsArchiveBusy)
        {
            return;
        }

        var session = await EnsureSessionAsync();
        if (session is null)
        {
            return;
        }

        IsArchiveBusy = true;
        SyncError = null;
        HasSyncMessage = false;
        ArchiveError = null;
        ArchiveOptions = Array.Empty<ArchiveOption>();
        SelectedArchive = null;

        try
        {
            var archives = await _sync.ListArchivesAsync(session);
            if (archives.Count == 0)
            {
                SyncMessage = "В истории пока нет версий.";
                HasSyncMessage = true;
                return;
            }

            ArchiveOptions = archives.Select(info => new ArchiveOption(info, ArchiveText(info))).ToList();
            SelectedArchive = ArchiveOptions[0];
            IsArchiveOpen = true;
        }
        catch (SyncException ex)
        {
            ArchiveError = ex.Message;

            // Диалог открывается с ошибкой: список версий устарел или недоступен,
            // и повторить чтение можно той же кнопкой в карточке.
            IsArchiveOpen = true;
        }
        catch (Exception ex)
        {
            ArchiveError = "Не удалось прочитать историю облака: " + ex.Message;
            IsArchiveOpen = true;
        }
        finally
        {
            IsArchiveBusy = false;
        }
    }

    /// <summary>Заменяет локальные данные выбранной версией из истории облака.</summary>
    [RelayCommand]
    private async Task ConfirmRestoreArchiveAsync()
    {
        IsArchiveOpen = false;

        var archive = SelectedArchive;
        SelectedArchive = null;
        ArchiveError = null;

        if (archive is null || IsSyncBusy)
        {
            return;
        }

        var session = await EnsureSessionAsync();
        if (session is null)
        {
            return;
        }

        IsSyncBusy = true;
        SyncError = null;
        HasSyncMessage = false;

        try
        {
            var snapshot = await _sync.DownloadArchiveAsync(archive.Info.Id, session);

            if (snapshot.SchemaVersion != SnapshotReader.DatabaseSchemaVersion)
            {
                SyncError = $"Данные в облаке сохранены версией схемы {snapshot.SchemaVersion}, " +
                            $"а приложение понимает {SnapshotReader.DatabaseSchemaVersion}. " +
                            "Обновите приложение.";
                return;
            }

            var backup = _settings.CreateBackupNow();
            var imported = new SnapshotImporter(_settings.Database).Import(snapshot);
            var rows = $"{imported} {Formats.Plural(imported, "строка", "строки", "строк")}";
            var moment = CloudMoment(archive.Info.ArchivedAt);

            SyncMessage = backup is null
                ? $"Восстановлено {rows} из версии от {moment}."
                : $"Восстановлено {rows} из версии от {moment}. Резервная копия: {Path.GetFileName(backup)}.";
            HasSyncMessage = true;
            IsSyncRestartNeeded = true;
            ReloadOrderCounts();
            OrdersChanged?.Invoke();
            await RefreshDataMomentsAsync();
        }
        catch (SyncException ex)
        {
            SyncError = ex.Message;
        }
        catch (Exception ex)
        {
            SyncError = "Не удалось восстановить версию из истории: " + ex.Message;
        }
        finally
        {
            IsSyncBusy = false;
        }
    }

    [RelayCommand]
    private void CancelArchive()
    {
        IsArchiveOpen = false;
        SelectedArchive = null;
        ArchiveError = null;
    }

    /// <summary>
    /// Возвращает действующую сессию, обновляя протухший токен.
    /// Возвращает null, если сессии нет или обновить её не удалось.
    /// </summary>
    private async Task<AuthSession?> EnsureSessionAsync()
    {
        var session = _sessions.Load();
        if (session is null)
        {
            SyncError = "Войдите в аккаунт, чтобы синхронизировать данные.";
            HasSyncMessage = false;
            return null;
        }

        if (!session.IsExpired(DateTimeOffset.UtcNow))
        {
            return session;
        }

        try
        {
            var refreshed = await _auth.RefreshAsync(session.RefreshToken);
            _sessions.Save(refreshed);
            return refreshed;
        }
        catch (AuthException ex)
        {
            _sessions.Clear();
            IsSignedIn = false;
            SignedInEmail = string.Empty;
            SyncError = ex.Message;
            HasSyncMessage = false;
            return null;
        }
    }

    [RelayCommand]
    private void OpenBackupFolder()
    {
        try
        {
            Directory.CreateDirectory(_settings.BackupDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_settings.BackupDirectory}\"")
            {
                UseShellExecute = true,
            });
            BackupError = null;
        }
        catch (Exception)
        {
            BackupError = "Не удалось открыть папку с бэкапами.";
        }
    }

    [RelayCommand]
    private void ClearReports()
    {
        if (!HasArchivedOrders)
        {
            DangerMessage = "В архиве нет заказов — очищать нечего.";
            HasDangerMessage = true;
            return;
        }

        _confirmAction = () =>
        {
            try
            {
                var deleted = _orders.DeleteArchivedOrders();
                DangerError = null;
                DangerMessage = $"Удалено {deleted} {Formats.Plural(deleted, "заказ", "заказа", "заказов")} " +
                                "из архива. Отчёты обнулены; текущие заказы и закупки остались.";
                HasDangerMessage = true;
                ReloadOrderCounts();
                OrdersChanged?.Invoke();
            }
            catch (Exception ex)
            {
                DangerError = "Не удалось очистить архив: " + ex.Message;
            }
        };

        var archived = ArchivedCount();
        ConfirmText = $"Удалить {archived} " +
                      $"{Formats.Plural(archived, "архивный заказ", "архивных заказа", "архивных заказов")}? " +
                      "Отчёты обнулятся. Текущие заказы, ингредиенты и закупки останутся.";
        ConfirmButtonText = "Очистить архив";
        IsConfirmOpen = true;
    }

    [RelayCommand]
    private void ClearAllOrders()
    {
        if (!HasAnyOrders)
        {
            DangerMessage = "Заказов нет — удалять нечего.";
            HasDangerMessage = true;
            return;
        }

        _confirmAction = () =>
        {
            try
            {
                var deleted = _orders.DeleteAllOrders();
                DangerError = null;
                DangerMessage = $"Удалено {deleted} {Formats.Plural(deleted, "заказ", "заказа", "заказов")}. " +
                                "Отчёты также обнулены; рецепты, ингредиенты и закупки остались.";
                HasDangerMessage = true;
                ReloadOrderCounts();
                OrdersChanged?.Invoke();
            }
            catch (Exception ex)
            {
                DangerError = "Не удалось удалить заказы: " + ex.Message;
            }
        };

        var total = TotalCount();
        ConfirmText = $"Удалить все {total} " +
                      $"{Formats.Plural(total, "заказ", "заказа", "заказов")}, включая текущие? " +
                      "Отчёты также обнулятся. Действие необратимо. Ингредиенты, рецепты и закупки останутся.";
        ConfirmButtonText = "Удалить все заказы";
        IsConfirmOpen = true;
    }

    [RelayCommand]
    private void ConfirmDanger()
    {
        IsConfirmOpen = false;
        _confirmAction?.Invoke();
        _confirmAction = null;
    }

    [RelayCommand]
    private void CancelConfirm()
    {
        IsConfirmOpen = false;
        _confirmAction = null;
    }

    private void ReloadOrderCounts()
    {
        var orders = _orders.GetOrders();
        var archived = orders.Count(order => OrderStatuses.IsFinal(order.Status));

        HasArchivedOrders = archived > 0;
        HasAnyOrders = orders.Count > 0;
        HasCurrentOrders = orders.Count > archived;
        OrdersSummary = orders.Count == 0
            ? "Заказов пока нет"
            : $"В архиве: {archived} · Всего: {orders.Count}";
    }

    private int ArchivedCount() =>
        _orders.GetOrders().Count(order => OrderStatuses.IsFinal(order.Status));

    private int TotalCount() => _orders.GetOrders().Count;

    private static string ThemeName(string theme) => theme switch
    {
        SettingsService.ThemeDark => "Тёмная",
        SettingsService.ThemeSystem => "Как в системе",
        _ => "Светлая",
    };
}

/// <summary>Версия из истории облака для выпадающего списка.</summary>
public sealed class ArchiveOption
{
    public ArchiveOption(SnapshotArchiveInfo info, string text)
    {
        Info = info;
        Text = text;
    }

    public SnapshotArchiveInfo Info { get; }

    public string Text { get; }

    public override string ToString() => Text;
}
