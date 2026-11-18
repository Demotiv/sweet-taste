using System.Globalization;
using Microsoft.Data.Sqlite;
using SweetTaste.Core.Services;

namespace SweetTaste.Core.Storage;

/// <summary>Файл базы SQLite и служебные операции: миграции и ежедневный бэкап.</summary>
public sealed class AppDatabase
{
    private const int BackupRetentionDays = 14;

    public AppDatabase(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = Path.GetFullPath(filePath);
    }

    public string FilePath { get; }

    public string DirectoryPath => Path.GetDirectoryName(FilePath)!;

    public string BackupDirectory => Path.Combine(DirectoryPath, "backups");

    public static AppDatabase CreateDefault() =>
        new(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SweetTaste",
            "sweet-taste.db"));

    public SqliteConnection Open()
    {
        Directory.CreateDirectory(DirectoryPath);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Pooling = false,
            ForeignKeys = true,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    public void Initialize()
    {
        Directory.CreateDirectory(DirectoryPath);

        if (File.Exists(FilePath))
        {
            TryCreateDailyBackup(DateOnly.FromDateTime(DateTime.Now), ReadBackupRetentionDays());
        }

        using var connection = Open();
        DatabaseMigrator.Migrate(connection);
    }

    /// <summary>Одна резервная копия в день. Возвращает путь к созданному файлу или null.</summary>
    public string? TryCreateDailyBackup(DateOnly today, int retentionDays = BackupRetentionDays)
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(BackupDirectory);
            var target = Path.Combine(BackupDirectory, $"sweet-taste-{today:yyyy-MM-dd}.db");
            if (File.Exists(target))
            {
                CleanupOldBackups(today, retentionDays);
                return null;
            }

            File.Copy(FilePath, target, overwrite: false);
            CleanupOldBackups(today, retentionDays);
            return target;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Бэкап по кнопке: файл с меткой времени, не перезаписывает дневной.</summary>
    public string? CreateBackupNow(int retentionDays)
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(BackupDirectory);
            var stamp = DateTime.Now.ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture);
            var target = Path.Combine(BackupDirectory, $"sweet-taste-{stamp}.db");
            var index = 1;

            while (File.Exists(target))
            {
                target = Path.Combine(BackupDirectory, $"sweet-taste-{stamp}-{index++}.db");
            }

            File.Copy(FilePath, target, overwrite: false);
            CleanupOldBackups(DateOnly.FromDateTime(DateTime.Now), Math.Clamp(retentionDays, 1, 365));
            return target;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Создаёт консистентную копию базы в указанный файл (VACUUM INTO).
    /// Копия содержит все данные, профиль, клиентов и настройки.
    /// </summary>
    public string ExportTo(string destinationPath)
    {
        var fullPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(fullPath);

        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("Не удалось определить папку для экспорта.", nameof(destinationPath));
        }

        Directory.CreateDirectory(directory);

        if (File.Exists(fullPath))
        {
            throw new IOException("Файл для экспорта уже существует.");
        }

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "VACUUM INTO $path;";
        command.Parameters.AddWithValue("$path", fullPath);
        command.ExecuteNonQuery();

        return fullPath;
    }

    /// <summary>
    /// Заменяет текущую базу выбранным файлом. Перед заменой создаёт резервную копию.
    /// Возвращает путь к копии старой базы (или null, если базы ещё не было).
    /// </summary>
    public string? RestoreFrom(string sourcePath, int retentionDays)
    {
        var source = Path.GetFullPath(sourcePath);

        if (!File.Exists(source))
        {
            throw new ValidationException("Файл базы не найден.");
        }

        if (string.Equals(source, FilePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ValidationException("Выбрана текущая база приложения. Укажите файл резервной копии.");
        }

        ValidateSqliteDatabase(source);

        var backup = CreateBackupNow(retentionDays);
        var temporary = FilePath + ".restore";

        try
        {
            Directory.CreateDirectory(DirectoryPath);

            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }

            File.Copy(source, temporary, overwrite: true);
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (IOException)
                {
                }
            }
        }

        return backup;
    }

    private static void ValidateSqliteDatabase(string path)
    {
        var header = new byte[16];

        using (var stream = File.OpenRead(path))
        {
            if (stream.Read(header, 0, header.Length) != header.Length ||
                !header.AsSpan().SequenceEqual("SQLite format 3\0"u8))
            {
                throw new ValidationException("Это не файл базы SQLite.");
            }
        }

        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString();

            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            using (var versionCommand = connection.CreateCommand())
            {
                versionCommand.CommandText = "PRAGMA user_version;";
                var version = Convert.ToInt32(versionCommand.ExecuteScalar());

                if (version < 1 || version > DatabaseMigrator.CurrentVersion)
                {
                    throw new ValidationException(
                        $"Версия базы {version} не поддерживается этой версией приложения.");
                }
            }

            using var tableCommand = connection.CreateCommand();
            tableCommand.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'ingredients';";
            if (Convert.ToInt32(tableCommand.ExecuteScalar()) == 0)
            {
                throw new ValidationException("В файле нет данных Sweet Taste.");
            }
        }
        catch (SqliteException)
        {
            throw new ValidationException("Не удалось прочитать файл базы.");
        }
    }

    private int ReadBackupRetentionDays()
    {
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM app_settings WHERE key = 'backup_retention_days' LIMIT 1;";
            var value = command.ExecuteScalar() as string;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) &&
                   days is >= 1 and <= 365
                ? days
                : BackupRetentionDays;
        }
        catch (SqliteException)
        {
            return BackupRetentionDays;
        }
    }

    private void CleanupOldBackups(DateOnly today, int retentionDays)
    {
        foreach (var file in Directory.EnumerateFiles(BackupDirectory, "sweet-taste-*.db"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var datePart = name["sweet-taste-".Length..];
            if (datePart.Length > 10)
            {
                datePart = datePart[..10];
            }

            if (DateOnly.TryParseExact(datePart, "yyyy-MM-dd", out var date) &&
                date < today.AddDays(-retentionDays))
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // Не мешаем запуску из-за старого бэкапа.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
