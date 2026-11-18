using System.Runtime.Versioning;
using SweetTaste.Core.Sync;

namespace SweetTaste.Core.Tests;

// DPAPI есть только на Windows, поэтому весь класс помечен как windows-only:
// иначе анализатор CA1416 ругается на каждый вызов в CI.
[SupportedOSPlatform("windows")]
public class ProtectedSessionStoreTests
{
    private static readonly Guid UserId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static AuthSession CreateSession(string access = "access-token", string refresh = "refresh-token") =>
        new()
        {
            AccessToken = access,
            RefreshToken = refresh,
            UserId = UserId,
            Email = "anna@example.ru",
            ExpiresAt = new DateTimeOffset(2026, 11, 18, 15, 30, 0, TimeSpan.Zero),
        };

    private static string CreateTempPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sweet-taste-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "sync-session.bin");
    }

    private static void Cleanup(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath)!;
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Load_WithoutFile_ReturnsNull()
    {
        var path = CreateTempPath();
        try
        {
            var store = new ProtectedSessionStore(path);

            Assert.Null(store.Load());
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void SaveThenLoad_RestoresEveryField()
    {
        var path = CreateTempPath();
        try
        {
            var store = new ProtectedSessionStore(path);
            var session = CreateSession();

            store.Save(session);
            var restored = store.Load();

            Assert.NotNull(restored);
            Assert.Equal(session.AccessToken, restored.AccessToken);
            Assert.Equal(session.RefreshToken, restored.RefreshToken);
            Assert.Equal(session.UserId, restored.UserId);
            Assert.Equal(session.Email, restored.Email);
            Assert.Equal(session.ExpiresAt, restored.ExpiresAt);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Save_WritesEncryptedFileWithoutPlainTokens()
    {
        var path = CreateTempPath();
        try
        {
            var store = new ProtectedSessionStore(path);

            store.Save(CreateSession(access: "PLAIN-ACCESS", refresh: "PLAIN-REFRESH"));

            var bytes = File.ReadAllBytes(path);
            var text = System.Text.Encoding.UTF8.GetString(bytes);
            Assert.DoesNotContain("PLAIN-ACCESS", text);
            Assert.DoesNotContain("PLAIN-REFRESH", text);
            Assert.DoesNotContain("anna@example.ru", text);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Save_OverwritesPreviousSession()
    {
        var path = CreateTempPath();
        try
        {
            var store = new ProtectedSessionStore(path);
            store.Save(CreateSession(access: "first"));
            store.Save(CreateSession(access: "second"));

            Assert.Equal("second", store.Load()!.AccessToken);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Save_LeavesNoTemporaryFile()
    {
        var path = CreateTempPath();
        try
        {
            var store = new ProtectedSessionStore(path);
            store.Save(CreateSession());

            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Clear_RemovesSession()
    {
        var path = CreateTempPath();
        try
        {
            var store = new ProtectedSessionStore(path);
            store.Save(CreateSession());

            store.Clear();

            Assert.False(File.Exists(path));
            Assert.Null(store.Load());
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Clear_WithoutFile_DoesNotThrow()
    {
        var path = CreateTempPath();
        try
        {
            var store = new ProtectedSessionStore(path);

            store.Clear();

            Assert.Null(store.Load());
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Load_CorruptedFile_ReturnsNull()
    {
        var path = CreateTempPath();
        try
        {
            File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            var store = new ProtectedSessionStore(path);

            Assert.Null(store.Load());
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void CreateDefault_PutsFileNextToDatabase()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sweet-taste-session-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = ProtectedSessionStore.CreateDefault(directory);
            store.Save(CreateSession());

            Assert.True(File.Exists(Path.Combine(directory, "sync-session.bin")));
        }
        finally
        {
            Cleanup(Path.Combine(directory, "sync-session.bin"));
        }
    }
}
