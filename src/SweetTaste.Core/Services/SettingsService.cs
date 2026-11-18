using System.Globalization;
using Microsoft.Data.Sqlite;
using SweetTaste.Core.Domain;
using SweetTaste.Core.Storage;

namespace SweetTaste.Core.Services;

/// <summary>Настройки приложения: тема, резервные копии и значения для новых рецептов.</summary>
public sealed class SettingsService
{
    public const string ThemeLight = "light";
    public const string ThemeDark = "dark";
    public const string ThemeSystem = "system";

    private const string ThemeKey = "theme";
    private const string RetentionKey = "backup_retention_days";
    private const string MarkupKey = "default_markup_percent";
    private const string UnitKey = "default_recipe_unit";
    private const string ScaleKey = "ui_scale_percent";
    private const int DefaultRetentionDays = 14;

    private readonly AppDatabase _database;
    private readonly Dictionary<string, string> _values;

    public SettingsService(AppDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _database.Initialize();
        _values = LoadValues();
    }

    public string DatabasePath => _database.FilePath;

    /// <summary>База приложения — нужна синхронизации для снапшота и импорта.</summary>
    public AppDatabase Database => _database;

    public string BackupDirectory => _database.BackupDirectory;

    public string Theme
    {
        get
        {
            var theme = Get(ThemeKey, ThemeLight);
            return theme is ThemeLight or ThemeDark or ThemeSystem ? theme : ThemeLight;
        }
        set
        {
            var normalized = value switch
            {
                ThemeDark => ThemeDark,
                ThemeSystem => ThemeSystem,
                _ => ThemeLight,
            };
            Set(ThemeKey, normalized);
        }
    }

    public int BackupRetentionDays
    {
        get => ParseInt(Get(RetentionKey, DefaultRetentionDays.ToString()), DefaultRetentionDays);
        set
        {
            if (value is < 1 or > 365)
            {
                throw new ValidationException("Срок хранения бэкапов — от 1 до 365 дней.");
            }

            Set(RetentionKey, value.ToString());
        }
    }

    public int DefaultMarkupPercent
    {
        get => ParseInt(Get(MarkupKey, "100"), 100);
        set
        {
            if (value is < 0 or > 1_000)
            {
                throw new ValidationException("Наценка должна быть от 0 до 1000%.");
            }

            Set(MarkupKey, value.ToString());
        }
    }

    public string DefaultRecipeUnit
    {
        get
        {
            var unit = Get(UnitKey, RecipeUnits.Pieces);
            return RecipeUnits.IsAllowed(unit) ? unit : RecipeUnits.Pieces;
        }
        set
        {
            if (!RecipeUnits.IsAllowed(value))
            {
                throw new ValidationException("Выход нового рецепта — шт, кг или г.");
            }

            Set(UnitKey, RecipeUnits.Normalize(value));
        }
    }

    public int UiScalePercent
    {
        get => ParseInt(Get(ScaleKey, "100"), 100);
        set
        {
            if (value is < 80 or > 125)
            {
                throw new ValidationException("Масштаб интерфейса — от 80 до 125%.");
            }

            Set(ScaleKey, value.ToString());
        }
    }

    /// <summary>Когда данные последний раз менялись на этом устройстве; null — ещё не менялись.</summary>
    /// <remarks>
    /// Читается из базы при каждом обращении, а не из кеша <c>_values</c>: отметку ставят
    /// триггеры SQLite, и словарь, снятый при запуске, её не увидит.
    /// </remarks>
    public DateTimeOffset? DataChangedAt
    {
        get
        {
            try
            {
                using var connection = _database.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT value FROM app_settings WHERE key = 'data_changed_at' LIMIT 1;";

                return command.ExecuteScalar() is string value &&
                       DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var moment)
                    ? moment
                    : null;
            }
            catch (SqliteException)
            {
                return null;
            }
        }
    }

    /// <summary>Ручной бэкап с меткой времени. Возвращает путь или null, если базы ещё нет.</summary>
    public string? CreateBackupNow() => _database.CreateBackupNow(BackupRetentionDays);

    /// <summary>Заменяет базу выбранным файлом, предварительно сохраняя текущую.</summary>
    public string? RestoreDatabase(string sourcePath) =>
        _database.RestoreFrom(sourcePath, BackupRetentionDays);

    private Dictionary<string, string> LoadValues()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM app_settings;";

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result[reader.GetString(0)] = reader.GetString(1);
        }

        return result;
    }

    private string Get(string key, string fallback) =>
        _values.TryGetValue(key, out var value) && value.Length > 0 ? value : fallback;

    private void Set(string key, string value)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO app_settings (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();

        _values[key] = value;
    }

    private static int ParseInt(string value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
}
