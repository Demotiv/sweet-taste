using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SweetTaste.Core.Domain;
using SweetTaste.Core.Services;
using SweetTaste.Core.Sync;
using SweetTaste.Core.Updates;
using SweetTaste.Desktop.Updates;

namespace SweetTaste.Desktop.ViewModels;

/// <summary>Главное окно: боковое меню и текущий раздел.</summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly InventoryService _inventory;
    private readonly RecipeService _recipes;
    private readonly OrderService _orders;
    private readonly ReportService _reports;
    private readonly PackagingService _packaging;
    private readonly SettingsService _settings;
    private readonly ProfileService _profile;
    private readonly CustomerService _customers;
    private readonly UpdateService _updates;
    private readonly SupabaseAuthClient _auth;
    private readonly ISessionStore _sessions;
    private readonly SyncService _sync;
    private AppUpdate? _availableUpdate;
    private UpdateVersion? _dismissedUpdateVersion;
    private bool _checkingUpdates;

    [ObservableProperty]
    private ViewModelBase? _currentPage;

    [ObservableProperty]
    private bool _isIngredientsActive;

    [ObservableProperty]
    private bool _isRecipesActive;

    [ObservableProperty]
    private bool _isTrashActive;

    [ObservableProperty]
    private bool _isOrdersActive;

    [ObservableProperty]
    private bool _isReportsActive;

    [ObservableProperty]
    private bool _isSettingsActive;

    /// <summary>Окно закрывается и ждёт выгрузку в облако — показывается заставка.</summary>
    [ObservableProperty]
    private bool _isExitUploadActive;

    /// <summary>Пользователь не хочет ждать выгрузку при закрытии.</summary>
    public event Action? ExitUploadSkipRequested;

    [RelayCommand]
    private void SkipExitUpload() => ExitUploadSkipRequested?.Invoke();

    [ObservableProperty]
    private string _brandName = "Sweet Taste";

    [ObservableProperty]
    private string _ownerName = "Кондитер";

    [ObservableProperty]
    private string _profileInitials = "К";

    [ObservableProperty]
    private Bitmap? _profilePhoto;

    [ObservableProperty]
    private bool _hasProfilePhoto;

    [ObservableProperty]
    private string _startupReminder = string.Empty;

    [ObservableProperty]
    private bool _hasStartupReminder;

    [ObservableProperty]
    private bool _hasUpdate;

    [ObservableProperty]
    private string _updateTitle = string.Empty;

    [ObservableProperty]
    private string _updateMessage = string.Empty;

    [ObservableProperty]
    private string _updateActionText = "Обновить";

    [ObservableProperty]
    private bool _updateCanInstall = true;

    [ObservableProperty]
    private bool _isUpdateBusy;

    private string _reminderSignature = string.Empty;
    private string _dismissedReminderSignature = string.Empty;

    public string VersionText { get; } = AppInfo.VersionLine;

    public MainWindowViewModel(
        InventoryService inventory,
        RecipeService recipes,
        OrderService orders,
        ReportService reports,
        PackagingService packaging,
        SettingsService settings,
        ProfileService profile,
        CustomerService customers,
        UpdateService updates,
        SupabaseAuthClient auth,
        ISessionStore sessions,
        SyncService sync)
    {
        _inventory = inventory;
        _recipes = recipes;
        _orders = orders;
        _reports = reports;
        _packaging = packaging;
        _settings = settings;
        _profile = profile;
        _customers = customers;
        _updates = updates;
        _auth = auth;
        _sessions = sessions;
        _sync = sync;
        LoadProfile();
        LoadReminder();
        Navigate("ingredients");
    }

    public string DatabasePath => _inventory.Database.FilePath;

    [RelayCommand]
    private void OpenIngredients() => Navigate("ingredients");

    [RelayCommand]
    private void OpenRecipes() => Navigate("recipes");

    [RelayCommand]
    private void OpenTrash() => Navigate("trash");

    [RelayCommand]
    private void OpenOrders() => Navigate("orders");

    [RelayCommand]
    private void OpenReports() => Navigate("reports");

    [RelayCommand]
    private void OpenSettings() => Navigate("settings");

    [RelayCommand]
    private void OpenProfile() => Navigate("profile");

    [RelayCommand]
    private void DismissReminder()
    {
        _dismissedReminderSignature = _reminderSignature;
        HasStartupReminder = false;
    }

    public async Task CheckForUpdatesAsync(bool manual = false)
    {
        if (_checkingUpdates || IsUpdateBusy)
        {
            return;
        }

        if (!_updates.CanSelfUpdate)
        {
            if (manual)
            {
                ShowUpdateNotice(
                    "Обновления недоступны",
                    "Автообновление работает только в собранной версии приложения из ZIP.",
                    canInstall: false);
            }

            return;
        }

        _checkingUpdates = true;

        try
        {
            var update = await _updates.GetLatestAsync();

            if (update is null)
            {
                if (manual)
                {
                    ShowUpdateNotice("Обновления", "Не удалось найти подходящий релиз на GitHub.", canInstall: false);
                }

                return;
            }

            if (!UpdateVersion.TryParse(AppInfo.Version, out var current) ||
                update.Version.CompareTo(current) <= 0)
            {
                if (manual)
                {
                    ShowUpdateNotice(
                        "Обновления",
                        $"Установлена последняя версия {AppInfo.Version}.",
                        canInstall: false);
                }

                return;
            }

            if (!manual &&
                _dismissedUpdateVersion is { } dismissed &&
                update.Version.CompareTo(dismissed) == 0)
            {
                return;
            }

            _availableUpdate = update;
            UpdateTitle = $"Доступна версия {update.Version}";
            UpdateMessage = $"Сейчас установлена {AppInfo.Version}. " +
                            "Архив скачается с GitHub, приложение закроется и запустится уже новой версией.";
            UpdateActionText = "Обновить";
            UpdateCanInstall = true;
            IsUpdateBusy = false;
            HasUpdate = true;
        }
        catch (Exception ex)
        {
            if (manual)
            {
                ShowUpdateNotice("Не удалось проверить обновления", ex.Message, canInstall: false);
            }
        }
        finally
        {
            _checkingUpdates = false;
        }
    }

    [RelayCommand]
    private void DismissUpdate()
    {
        if (IsUpdateBusy)
        {
            return;
        }

        _dismissedUpdateVersion = _availableUpdate?.Version;
        HasUpdate = false;
    }

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (_availableUpdate is not { } update || IsUpdateBusy)
        {
            return;
        }

        IsUpdateBusy = true;
        UpdateCanInstall = false;
        UpdateTitle = $"Скачивание версии {update.Version}";
        UpdateMessage = "0%";
        UpdateActionText = "Скачивание…";

        try
        {
            var progress = new Progress<double>(value =>
            {
                UpdateMessage = $"{value * 100:0}%";
            });

            var downloaded = await _updates.DownloadAsync(update, progress);

            UpdateTitle = "Установка обновления";
            UpdateMessage = "Архив проверен. Приложение сейчас закроется и запустится заново.";
            UpdateActionText = "Установка…";

            await Task.Delay(600);
            _updates.StartUpdater(DatabasePath, downloaded, update);

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
        }
        catch (Exception ex)
        {
            IsUpdateBusy = false;
            UpdateCanInstall = true;
            UpdateActionText = "Повторить";
            UpdateTitle = "Не удалось обновить";
            UpdateMessage = ex.Message;
            HasUpdate = true;
        }
    }

    private void ShowUpdateNotice(string title, string message, bool canInstall)
    {
        UpdateTitle = title;
        UpdateMessage = message;
        UpdateActionText = canInstall ? "Обновить" : "Закрыть";
        UpdateCanInstall = canInstall;
        IsUpdateBusy = false;
        HasUpdate = true;
    }

    private void LoadReminder()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var active = _orders.GetOrders()
            .Where(order => !OrderStatuses.IsFinal(order.Status))
            .ToList();

        var todayCount = active.Count(order => order.DueDate == today);
        var tomorrowCount = active.Count(order => order.DueDate == today.AddDays(1));
        var parts = new List<string>();

        if (todayCount > 0)
        {
            parts.Add($"сегодня {todayCount} {Formats.Plural(todayCount, "заказ", "заказа", "заказов")}");
        }

        if (tomorrowCount > 0)
        {
            parts.Add($"завтра {tomorrowCount} {Formats.Plural(tomorrowCount, "заказ", "заказа", "заказов")}");
        }

        _reminderSignature = $"{todayCount}:{tomorrowCount}";
        HasStartupReminder = parts.Count > 0 && _reminderSignature != _dismissedReminderSignature;
        StartupReminder = parts.Count > 0 ? "К выдаче: " + string.Join(" · ", parts) : string.Empty;
    }

    public void Navigate(string section)
    {
        ViewModelBase page = section switch
        {
            "recipes" => new RecipesViewModel(_recipes, _settings),
            "orders" => new OrdersViewModel(_orders, _recipes, _packaging, _customers),
            "reports" => new ReportsViewModel(_reports),
            "settings" => new SettingsViewModel(_settings, _orders, _auth, _sessions, _sync),
            "profile" => new ProfileViewModel(_profile, LoadProfile),
            "trash" => new TrashViewModel(_inventory, _recipes),
            _ => new IngredientsViewModel(_inventory, _packaging),
        };

        if (page is OrdersViewModel ordersPage)
        {
            ordersPage.OrdersChanged += LoadReminder;
        }
        else if (page is SettingsViewModel settingsPage)
        {
            settingsPage.OrdersChanged += LoadReminder;
            settingsPage.CheckUpdatesRequested += () => _ = CheckForUpdatesAsync(manual: true);
        }

        CurrentPage = page;
        LoadReminder();

        IsIngredientsActive = section == "ingredients";
        IsRecipesActive = section == "recipes";
        IsOrdersActive = section == "orders";
        IsReportsActive = section == "reports";
        IsSettingsActive = section == "settings";
        IsTrashActive = section == "trash";
    }

    private void LoadProfile()
    {
        var profile = _profile.GetProfile();

        BrandName = string.IsNullOrWhiteSpace(profile.Name) ? "Sweet Taste" : profile.Name;
        OwnerName = string.IsNullOrWhiteSpace(profile.OwnerName) ? "Кондитер" : profile.OwnerName;
        ProfileInitials = Initials(profile.OwnerName, profile.Name);
        ProfilePhoto = TryLoadBitmap(profile.Photo);
        HasProfilePhoto = ProfilePhoto is not null;
    }

    private static string Initials(string ownerName, string name)
    {
        var source = string.IsNullOrWhiteSpace(ownerName) ? name : ownerName;
        var parts = source.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return parts.Length switch
        {
            0 => "К",
            1 => parts[0][..1].ToUpperInvariant(),
            _ => (parts[0][..1] + parts[1][..1]).ToUpperInvariant(),
        };
    }

    private static Bitmap? TryLoadBitmap(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(bytes);
            return new Bitmap(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
