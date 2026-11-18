using SweetTaste.Core.Services;
using SweetTaste.Core.Storage;

namespace SweetTaste.Core.Tests;

/// <summary>Временная база в отдельной папке, удаляется после теста.</summary>
internal sealed class TestDatabase : IDisposable
{
    public TestDatabase()
    {
        Directory = Path.Combine(Path.GetTempPath(), "sweet-taste-tests-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        Database = new AppDatabase(Path.Combine(Directory, "test.db"));
        Service = new InventoryService(Database);
    }

    public string Directory { get; }

    public AppDatabase Database { get; }

    public InventoryService Service { get; }

    public InventoryService Reopen() => new(new AppDatabase(Database.FilePath));

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // Уборка не должна влиять на результат теста.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
