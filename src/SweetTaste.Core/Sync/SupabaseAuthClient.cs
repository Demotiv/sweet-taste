using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SweetTaste.Core.Sync;

/// <summary>Вход по email и паролю через Supabase Auth.</summary>
/// <remarks>
/// Регистрация закрыта: аккаунт создаётся вручную в панели Supabase, приложение
/// умеет только входить и обновлять сессию. Пароль нигде не сохраняется.
/// </remarks>
public sealed class SupabaseAuthClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly SupabaseOptions _options;
    private readonly bool _ownsHttp;

    public SupabaseAuthClient(SupabaseOptions options, HttpClient? http = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<AuthSession> SignInAsync(
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var request = new HttpRequestMessage(
            HttpMethod.Post,
            _options.AuthUrl + "/token?grant_type=password")
        {
            Content = JsonContent.Create(new { email = email.Trim(), password }),
        };
        AddApiKey(request);

        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw CreateSignInError(response.StatusCode, body);
        }

        return ParseSession(body);
    }

    public async Task<AuthSession> RefreshAsync(
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);

        var request = new HttpRequestMessage(
            HttpMethod.Post,
            _options.AuthUrl + "/token?grant_type=refresh_token")
        {
            Content = JsonContent.Create(new { refresh_token = refreshToken }),
        };
        AddApiKey(request);

        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw CreateRefreshError(response.StatusCode, body);
        }

        return ParseSession(body);
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    private void AddApiKey(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("apikey", _options.PublishableKey);
    }

    private static AuthSession ParseSession(string body)
    {
        AuthResponse? payload;
        try
        {
            payload = JsonSerializer.Deserialize<AuthResponse>(body);
        }
        catch (JsonException exception)
        {
            throw new AuthException("Сервер вернул неожиданный ответ.", inner: exception);
        }

        if (payload?.AccessToken is null || payload.RefreshToken is null || payload.User is null)
        {
            throw new AuthException("Сервер вернул неполную сессию.");
        }

        if (!Guid.TryParse(payload.User.Id, out var userId))
        {
            throw new AuthException("Сервер вернул некорректный идентификатор пользователя.");
        }

        var expiresAt = payload.ExpiresAt > 0
            ? DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresAt)
            : DateTimeOffset.UtcNow.AddSeconds(payload.ExpiresIn);

        return new AuthSession
        {
            AccessToken = payload.AccessToken,
            RefreshToken = payload.RefreshToken,
            UserId = userId,
            Email = payload.User.Email ?? string.Empty,
            ExpiresAt = expiresAt,
        };
    }

    private static AuthException CreateSignInError(HttpStatusCode status, string body)
    {
        var (code, message) = ReadError(body);

        if (status == HttpStatusCode.BadRequest && code == "invalid_credentials")
        {
            return new AuthException("Неверный email или пароль.", code);
        }

        if (code == "email_not_confirmed")
        {
            return new AuthException("Email не подтверждён. Подтвердите его в панели Supabase.", code);
        }

        if (status == HttpStatusCode.TooManyRequests)
        {
            return new AuthException("Слишком много попыток входа. Подождите минуту.", code);
        }

        if (status == HttpStatusCode.Unauthorized || status == HttpStatusCode.Forbidden)
        {
            return new AuthException("Сервер отклонил вход. Проверьте ключ проекта.", code);
        }

        return new AuthException(
            message is null ? "Не удалось войти. Проверьте интернет." : "Не удалось войти: " + message,
            code);
    }

    private static AuthException CreateRefreshError(HttpStatusCode status, string body)
    {
        var (code, message) = ReadError(body);

        if (status == HttpStatusCode.BadRequest || status == HttpStatusCode.Unauthorized)
        {
            return new AuthException("Сессия истекла. Войдите заново.", code);
        }

        return new AuthException(
            message is null ? "Не удалось обновить сессию." : "Не удалось обновить сессию: " + message,
            code);
    }

    private static (string? Code, string? Message) ReadError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var code = root.TryGetProperty("error_code", out var codeElement)
                ? codeElement.GetString()
                : root.TryGetProperty("error", out var errorElement)
                    ? errorElement.GetString()
                    : null;
            var message = root.TryGetProperty("msg", out var msgElement)
                ? msgElement.GetString()
                : root.TryGetProperty("message", out var messageElement)
                    ? messageElement.GetString()
                    : root.TryGetProperty("error_description", out var descriptionElement)
                        ? descriptionElement.GetString()
                        : null;
            return (code, message);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private sealed record AuthResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; init; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; init; }

        [JsonPropertyName("expires_in")]
        public long ExpiresIn { get; init; }

        [JsonPropertyName("expires_at")]
        public long ExpiresAt { get; init; }

        [JsonPropertyName("user")]
        public AuthUser? User { get; init; }
    }

    private sealed record AuthUser
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("email")]
        public string? Email { get; init; }
    }
}
