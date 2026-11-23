using SweetTaste.Core.Services;
using SweetTaste.Core.Storage;
using SweetTaste.Core.Sync;

namespace SweetTaste.Core.Tests;

public class ExitUploadGuardTests
{
    private static readonly DateTimeOffset CloudAt = new(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);

    private static CloudState Cloud(DateTimeOffset takenAt) => new()
    {
        TakenAt = takenAt,
        SchemaVersion = 15,
        TotalRows = 10,
        RowCounts = new Dictionary<string, int> { [SyncTables.Orders] = 10 },
    };

    [Fact]
    public void EmptyLocalDatabase_IsNeverUploaded()
    {
        Assert.NotNull(ExitUploadGuard.Check(0, null, null));
        Assert.NotNull(ExitUploadGuard.Check(0, Cloud(CloudAt), CloudAt));
    }

    [Fact]
    public void EmptyCloud_AcceptsUpload()
    {
        Assert.Null(ExitUploadGuard.Check(5, null, null));
    }

    [Fact]
    public void ComputerThatNeverSynced_DoesNotOverwriteCloud()
    {
        Assert.NotNull(ExitUploadGuard.Check(5, Cloud(CloudAt), null));
    }

    [Fact]
    public void CloudUpdatedElsewhere_DoesNotOverwriteCloud()
    {
        Assert.NotNull(ExitUploadGuard.Check(5, Cloud(CloudAt.AddMinutes(3)), CloudAt));
    }

    [Fact]
    public void CloudMatchesLastExchange_AcceptsUpload()
    {
        // Облако хранит микросекунды, локальная метка — тики: разница меньше миллисекунды.
        Assert.Null(ExitUploadGuard.Check(5, Cloud(CloudAt), CloudAt.AddTicks(7)));
    }

    [Fact]
    public void Settings_PersistSyncMarks()
    {
        using var db = new TestDatabase();
        var settings = new SettingsService(db.Database);

        Assert.Null(settings.CloudSnapshotSeenAt);
        Assert.Equal(string.Empty, settings.ExitUploadSkipReason);

        var moment = new DateTimeOffset(2026, 11, 20, 13, 0, 0, TimeSpan.FromHours(3)).AddTicks(1234567);
        settings.CloudSnapshotSeenAt = moment;
        settings.ExitUploadSkipReason = "причина";

        var reopened = new SettingsService(new AppDatabase(db.Database.FilePath));
        Assert.Equal(moment, reopened.CloudSnapshotSeenAt);
        Assert.Equal("причина", reopened.ExitUploadSkipReason);

        reopened.CloudSnapshotSeenAt = null;
        reopened.ExitUploadSkipReason = string.Empty;
        Assert.Null(reopened.CloudSnapshotSeenAt);
        Assert.Equal(string.Empty, reopened.ExitUploadSkipReason);
    }
}
