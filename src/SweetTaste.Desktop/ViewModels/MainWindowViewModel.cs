using System.IO;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SweetTaste.Core.Domain;
using SweetTaste.Core.Services;

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
        CustomerService customers)
    {
        _inventory = inventory;
        _recipes = recipes;
        _orders = orders;
        _reports = reports;
        _packaging = packaging;
        _settings = settings;
        _profile = profile;
        _customers = customers;
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
            "settings" => new SettingsViewModel(_settings, _orders),
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
