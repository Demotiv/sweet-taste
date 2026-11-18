using SweetTaste.Core.Domain;
using SweetTaste.Core.Services;

namespace SweetTaste.Core.Tests;

/// <summary>
/// Отметку «данные менялись» ставят триггеры миграции v16: карточка синхронизации
/// сравнивает её с временем облака.
/// </summary>
public class DataChangedTriggerTests
{
    [Fact]
    public void Migration16_CreatesTriggers()
    {
        using var db = new TestDatabase();
        _ = new SettingsService(db.Database);

        using var connection = db.Database.Open();

        using (var version = connection.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version;";
            Assert.Equal(16, Convert.ToInt32(version.ExecuteScalar()));
        }

        using var triggers = connection.CreateCommand();
        triggers.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger';";
        Assert.Equal(36, Convert.ToInt32(triggers.ExecuteScalar()));
    }

    [Fact]
    public void FreshDatabase_DataChangedAtIsNull()
    {
        using var db = new TestDatabase();
        var settings = new SettingsService(db.Database);

        Assert.Null(settings.DataChangedAt);
    }

    [Fact]
    public void Insert_UpdatesDataChangedAt()
    {
        using var db = new TestDatabase();
        var settings = new SettingsService(db.Database);

        db.Service.AddIngredient("Сахар", null, Units.Grams);

        var moment = settings.DataChangedAt;
        Assert.NotNull(moment);
        Assert.True(DateTimeOffset.UtcNow - moment.Value < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Update_UpdatesDataChangedAt()
    {
        using var db = new TestDatabase();
        var settings = new SettingsService(db.Database);
        var sugar = db.Service.AddIngredient("Сахар", null, Units.Grams);
        var backdated = Backdate(settings);

        db.Service.UpdateIngredient(sugar.Id, "Сахар тростниковый", null, Units.Grams);

        var moment = settings.DataChangedAt;
        Assert.NotNull(moment);
        Assert.True(moment.Value > backdated);
    }

    [Fact]
    public void Delete_UpdatesDataChangedAt()
    {
        using var db = new TestDatabase();
        var settings = new SettingsService(db.Database);
        var sugar = db.Service.AddIngredient("Сахар", null, Units.Grams);
        var backdated = Backdate(settings);

        db.Service.DeleteIngredient(sugar.Id);

        var moment = settings.DataChangedAt;
        Assert.NotNull(moment);
        Assert.True(moment.Value > backdated);
    }

    [Fact]
    public void AppSettings_DoesNotUpdateDataChangedAt()
    {
        using var db = new TestDatabase();
        var settings = new SettingsService(db.Database);

        settings.Theme = SettingsService.ThemeDark;
        settings.BackupRetentionDays = 30;

        Assert.Null(settings.DataChangedAt);
    }

    /// <summary>
    /// Отодвигает отметку в 2020 год: два действия подряд попадают в одну миллисекунду,
    /// и проверка «время изменилось» стала бы случайной. Возвращает отодвинутое значение.
    /// </summary>
    private static DateTimeOffset Backdate(SettingsService settings)
    {
        var moment = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

        using var connection = settings.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE app_settings SET value = $value WHERE key = 'data_changed_at';";
        command.Parameters.AddWithValue("$value", moment.ToString("O"));
        command.ExecuteNonQuery();

        return moment;
    }
}
