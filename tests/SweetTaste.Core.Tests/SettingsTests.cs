using Microsoft.Data.Sqlite;
using SweetTaste.Core.Domain;
using SweetTaste.Core.Services;
using SweetTaste.Core.Storage;

namespace SweetTaste.Core.Tests;

public class SettingsTests
{
    [Fact]
    public void Defaults_AreSensible()
    {
        using var db = new TestDatabase();
        var settings = new SettingsService(db.Database);

        Assert.Equal(SettingsService.ThemeLight, settings.Theme);
        Assert.Equal(14, settings.BackupRetentionDays);
        Assert.Equal(100, settings.DefaultMarkupPercent);
        Assert.Equal(RecipeUnits.Pieces, settings.DefaultRecipeUnit);
        Assert.Equal(100, settings.UiScalePercent);
    }

    [Fact]
    public void Values_PersistAfterReopen()
    {
        using var db = new TestDatabase();
        var settings = new SettingsService(db.Database);

        settings.Theme = SettingsService.ThemeDark;
        settings.BackupRetentionDays = 30;
        settings.DefaultMarkupPercent = 150;
        settings.DefaultRecipeUnit = RecipeUnits.Kilograms;

        var reopened = new SettingsService(new AppDatabase(db.Database.FilePath));

        Assert.Equal(SettingsService.ThemeDark, reopened.Theme);
        Assert.Equal(30, reopened.BackupRetentionDays);
        Assert.Equal(150, reopened.DefaultMarkupPercent);
        Assert.Equal(RecipeUnits.Kilograms, reopened.DefaultRecipeUnit);
    }

    [Fact]
    public void InvalidValues_AreRejectedOrFalledBack()
    {
        using var db = new TestDatabase();
        var settings = new SettingsService(db.Database);

        settings.Theme = "unknown";
        Assert.Equal(SettingsService.ThemeLight, settings.Theme);

        Assert.Throws<ValidationException>(() => settings.BackupRetentionDays = 0);
        Assert.Throws<ValidationException>(() => settings.BackupRetentionDays = 366);
        Assert.Throws<ValidationException>(() => settings.DefaultMarkupPercent = -1);
        Assert.Throws<ValidationException>(() => settings.DefaultMarkupPercent = 1001);
        Assert.Throws<ValidationException>(() => settings.DefaultRecipeUnit = "л");
    }

    [Fact]
    public void ManualBackup_CreatesTimestampedCopy()
    {
        using var db = new TestDatabase();
        var settings = new SettingsService(db.Database);

        var path = settings.CreateBackupNow();

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.StartsWith("sweet-taste-", Path.GetFileName(path));
        Assert.True(new FileInfo(path).Length > 0);
    }

    [Fact]
    public void DailyBackup_RemovesCopiesOlderThanRetention()
    {
        using var db = new TestDatabase();
        _ = new SettingsService(db.Database);

        Directory.CreateDirectory(db.Database.BackupDirectory);
        var old = Path.Combine(db.Database.BackupDirectory, "sweet-taste-2020-01-01.db");
        File.WriteAllText(old, "old");

        var today = DateOnly.FromDateTime(DateTime.Now);
        db.Database.TryCreateDailyBackup(today, retentionDays: 7);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(Path.Combine(db.Database.BackupDirectory, $"sweet-taste-{today:yyyy-MM-dd}.db")));
    }

    [Fact]
    public void Restore_ReplacesDatabaseAndKeepsBackup()
    {
        using var source = new TestDatabase();
        source.Service.AddIngredient("Сахар", null, Units.Grams);
        new SettingsService(source.Database).Theme = SettingsService.ThemeDark;

        using var target = new TestDatabase();
        target.Service.AddIngredient("Мука", null, Units.Grams);
        new SettingsService(target.Database).Theme = SettingsService.ThemeLight;

        var sourceCopy = Path.Combine(target.Directory, "restore-source.db");
        File.Copy(source.Database.FilePath, sourceCopy);

        var backup = target.Database.RestoreFrom(sourceCopy, 14);

        Assert.NotNull(backup);
        Assert.True(File.Exists(backup));

        var restoredInventory = new InventoryService(target.Database);
        var restoredSettings = new SettingsService(target.Database);

        Assert.Equal("Сахар", Assert.Single(restoredInventory.GetIngredients()).Ingredient.Name);
        Assert.Equal(SettingsService.ThemeDark, restoredSettings.Theme);
    }

    [Fact]
    public void Restore_RejectsBadFilesAndOwnDatabase()
    {
        using var db = new TestDatabase();
        _ = new SettingsService(db.Database);

        var text = Path.Combine(db.Directory, "not-a-db.txt");
        File.WriteAllText(text, "это не база");

        Assert.Throws<ValidationException>(() => db.Database.RestoreFrom(text, 14));
        Assert.Throws<ValidationException>(() =>
            db.Database.RestoreFrom(db.Database.FilePath, 14));
    }

    [Fact]
    public void Restore_RejectsFutureSchema()
    {
        using var source = new TestDatabase();
        _ = new SettingsService(source.Database);

        using var target = new TestDatabase();
        _ = new SettingsService(target.Database);

        var copy = Path.Combine(target.Directory, "future.db");
        File.Copy(source.Database.FilePath, copy);

        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
               {
                   DataSource = copy,
                   Pooling = false,
               }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 999;";
            command.ExecuteNonQuery();
        }

        Assert.Throws<ValidationException>(() => target.Database.RestoreFrom(copy, 14));
    }
}
