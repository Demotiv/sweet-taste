using System.Globalization;
using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using SweetTaste.Core.Services;
using SweetTaste.Core.Storage;
using SweetTaste.Core.Sync;
using SweetTaste.Desktop.Sync;
using SweetTaste.Desktop.Updates;
using SweetTaste.Desktop.ViewModels;
using SweetTaste.Desktop.Views;

namespace SweetTaste.Desktop;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var customPath = Environment.GetEnvironmentVariable("SWEET_TASTE_DB");
            var database = string.IsNullOrWhiteSpace(customPath)
                ? AppDatabase.CreateDefault()
                : new AppDatabase(customPath);

            var service = new InventoryService(database);
            var recipes = new RecipeService(database, service);
            var orders = new OrderService(database, recipes);
            var customers = new CustomerService(database);
            var reports = new ReportService(orders, service, customers);
            var packaging = new PackagingService(database);
            var settings = new SettingsService(database);
            var profile = new ProfileService(database);
            var updates = new UpdateService();

            var supabase = new SupabaseOptions
            {
                Url = "https://axifpwdzkxagfzplykcr.supabase.co",
                PublishableKey = "sb_publishable_0lSKaFUQ5x4v-hvVkKva_g_NOeNO3Gr",
            };
            var auth = new SupabaseAuthClient(supabase);
            var sync = new SyncService(supabase);
            var sessions = ProtectedSessionStore.CreateDefault(database.DirectoryPath);

            ApplyTheme(settings.Theme);

            var viewModel = new MainWindowViewModel(
                service, recipes, orders, reports, packaging, settings, profile, customers, updates,
                auth, sessions, sync);

            var window = new MainWindow
            {
                DataContext = viewModel,
            };

            window.Opened += async (_, _) => await viewModel.CheckForUpdatesAsync();

            desktop.ShutdownRequested += (_, _) =>
                SyncOnExit.Run(database, auth, sync, sessions, settings);

            desktop.Exit += (_, _) =>
            {
                updates.Dispose();
                auth.Dispose();
                sync.Dispose();
            };

            window.ApplyUiScale(settings.UiScalePercent / 100.0);
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Применяет светлую, тёмную тему или тему системы.</summary>
    public static void ApplyTheme(string theme)
    {
        if (Application.Current is not { } application)
        {
            return;
        }

        application.RequestedThemeVariant = theme switch
        {
            SettingsService.ThemeDark => ThemeVariant.Dark,
            SettingsService.ThemeSystem => ThemeVariant.Default,
            _ => ThemeVariant.Light,
        };
    }

    /// <summary>Масштабирует интерфейс главного окна и подстраивает размер окна.</summary>
    public static void ApplyUiScale(int percent)
    {
        if (Application.Current?.ApplicationLifetime is
            IClassicDesktopStyleApplicationLifetime { MainWindow: MainWindow window })
        {
            window.ApplyUiScale(percent / 100.0);
        }
    }
}
