namespace SweetTaste.Core.Sync;

/// <summary>Параметры подключения к Supabase. Ключ здесь только publishable.</summary>
public sealed record SupabaseOptions
{
    public required string Url { get; init; }

    public required string PublishableKey { get; init; }

    public string AuthUrl => Url.TrimEnd('/') + "/auth/v1";

    public string RestUrl => Url.TrimEnd('/') + "/rest/v1";
}

/// <summary>Сессия входа: токены и данные пользователя.</summary>
public sealed record AuthSession
{
    public required string AccessToken { get; init; }

    public required string RefreshToken { get; init; }

    public required Guid UserId { get; init; }

    public required string Email { get; init; }

    /// <summary>Когда истекает access token (UTC).</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Токен считается протухшим за минуту до истечения — запас на сеть.</summary>
    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt.AddMinutes(-1);
}

/// <summary>Ошибка входа или обновления сессии с понятным текстом для интерфейса.</summary>
public sealed class AuthException : Exception
{
    public AuthException(string message, string? code = null, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
    }

    /// <summary>Код ошибки Supabase, например <c>invalid_credentials</c>.</summary>
    public string? Code { get; }
}
