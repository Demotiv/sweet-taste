using System;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SweetTaste.Core.Domain;
using SweetTaste.Core.Services;

namespace SweetTaste.Desktop.ViewModels;

/// <summary>Настройки: тема, резервные копии, значения по умолчанию и очистка данных.</summary>
public sealed partial class SettingsViewModel : ViewModelBase, IFormPage
{
    private readonly SettingsService _settings;
    private readonly OrderService _orders;
    private Action? _confirmAction;

    /// <summary>Сообщает главному окну, что заказы изменились (напоминание о выдачах).</summary>
    public event Action? OrdersChanged;

    /// <summary>Просит главное окно проверить обновления вручную.</summary>
    public event Action? CheckUpdatesRequested;

    public string VersionText => AppInfo.VersionLine;

    public SettingsViewModel(SettingsService settings, OrderService orders)
    {
        _settings = settings;
        _orders = orders;

        _selectedTheme = ThemeName(settings.Theme);
        _selectedRetention = settings.BackupRetentionDays;
        _markup = settings.DefaultMarkupPercent;
        _recipeUnit = settings.DefaultRecipeUnit;
        _selectedScale = settings.UiScalePercent;
        _databasePath = settings.DatabasePath;
        ReloadOrderCounts();
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

    public string BackupFolder => _settings.BackupDirectory;

    public bool IsFormOpen => IsConfirmOpen;

    public void CancelForm()
    {
        if (IsConfirmOpen)
        {
            IsConfirmOpen = false;
            _confirmAction = null;
        }
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
