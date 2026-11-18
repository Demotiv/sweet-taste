using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SweetTaste.Core.Sync;

/// <summary>Хранит сессию входа между запусками приложения.</summary>
public interface ISessionStore
{
    AuthSession? Load();

    void Save(AuthSession session);

    void Clear();
}

/// <summary>
/// Сессия в файле рядом с базой, зашифрованная DPAPI под текущего пользователя Windows.
/// </summary>
/// <remarks>
/// Refresh token даёт доступ к данным в облаке, поэтому в открытом виде он не лежит.
/// DPAPI привязывает шифр к учётной записи Windows: скопированный файл на другом
/// компьютере не расшифруется.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ProtectedSessionStore : ISessionStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SweetTaste.Sync.Session.v1");

    private readonly string _filePath;

    public ProtectedSessionStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
    }

    public static ProtectedSessionStore CreateDefault(string databaseDirectory) =>
        new(Path.Combine(databaseDirectory, "sync-session.bin"));

    public AuthSession? Load()
    {
        if (!File.Exists(_filePath))
        {
            return null;
        }

        try
        {
            var protectedBytes = File.ReadAllBytes(_filePath);
            var plain = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            var stored = JsonSerializer.Deserialize<StoredSession>(plain);
            if (stored is null || string.IsNullOrWhiteSpace(stored.AccessToken) ||
                string.IsNullOrWhiteSpace(stored.RefreshToken) || !Guid.TryParse(stored.UserId, out var userId))
            {
                return null;
            }

            return new AuthSession
            {
                AccessToken = stored.AccessToken,
                RefreshToken = stored.RefreshToken,
                UserId = userId,
                Email = stored.Email ?? string.Empty,
                ExpiresAt = stored.ExpiresAt,
            };
        }
        catch (CryptographicException)
        {
            // Файл повреждён или создан другим пользователем Windows — считаем, что входа нет.
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Save(AuthSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var stored = new StoredSession
        {
            AccessToken = session.AccessToken,
            RefreshToken = session.RefreshToken,
            UserId = session.UserId.ToString(),
            Email = session.Email,
            ExpiresAt = session.ExpiresAt,
        };

        var plain = JsonSerializer.SerializeToUtf8Bytes(stored);
        var protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Пишем через временный файл: обрыв записи не оставит битую сессию.
        var temporary = _filePath + ".tmp";
        File.WriteAllBytes(temporary, protectedBytes);
        File.Move(temporary, _filePath, overwrite: true);
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                File.Delete(_filePath);
            }
        }
        catch (IOException)
        {
            // Не удалось удалить — при следующем входе файл будет перезаписан.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record StoredSession
    {
        public string? AccessToken { get; init; }

        public string? RefreshToken { get; init; }

        public string? UserId { get; init; }

        public string? Email { get; init; }

        public DateTimeOffset ExpiresAt { get; init; }
    }
}
