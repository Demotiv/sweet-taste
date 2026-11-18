using System.Net;
using System.Text;
using SweetTaste.Core.Sync;

namespace SweetTaste.Core.Tests;

public class SupabaseAuthClientTests
{
    private static readonly SupabaseOptions Options = new()
    {
        Url = "https://example.supabase.co",
        PublishableKey = "sb_publishable_test",
    };

    private static readonly Guid UserId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static string SessionJson(long expiresIn = 3600) =>
        $$"""
        {
          "access_token": "access-1",
          "refresh_token": "refresh-1",
          "expires_in": {{expiresIn}},
          "expires_at": 0,
          "user": { "id": "{{UserId}}", "email": "anna@example.ru" }
        }
        """;

    private static (SupabaseAuthClient Client, StubHandler Handler) CreateClient(
        HttpStatusCode status,
        string body)
    {
        var handler = new StubHandler(status, body);
        return (new SupabaseAuthClient(Options, new HttpClient(handler)), handler);
    }

    [Fact]
    public async Task SignIn_SendsPasswordGrantWithApiKey()
    {
        var (client, handler) = CreateClient(HttpStatusCode.OK, SessionJson());
        using var _ = client;

        await client.SignInAsync("anna@example.ru", "secret");

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://example.supabase.co/auth/v1/token?grant_type=password", request.Url);
        Assert.Equal("sb_publishable_test", request.ApiKey);
        Assert.Contains("\"email\":\"anna@example.ru\"", request.Body);
        Assert.Contains("\"password\":\"secret\"", request.Body);
    }

    [Fact]
    public async Task SignIn_ReturnsSessionWithUserAndExpiry()
    {
        var (client, _) = CreateClient(HttpStatusCode.OK, SessionJson(expiresIn: 3600));
        using var __ = client;

        var before = DateTimeOffset.UtcNow;
        var session = await client.SignInAsync("anna@example.ru", "secret");

        Assert.Equal("access-1", session.AccessToken);
        Assert.Equal("refresh-1", session.RefreshToken);
        Assert.Equal(UserId, session.UserId);
        Assert.Equal("anna@example.ru", session.Email);
        Assert.InRange(session.ExpiresAt, before.AddMinutes(59), before.AddMinutes(61));
    }

    [Fact]
    public async Task SignIn_TrimsEmail()
    {
        var (client, handler) = CreateClient(HttpStatusCode.OK, SessionJson());
        using var _ = client;

        await client.SignInAsync("  anna@example.ru  ", "secret");

        Assert.Contains("\"email\":\"anna@example.ru\"", Assert.Single(handler.Requests).Body);
    }

    [Fact]
    public async Task SignIn_WrongPassword_GivesReadableMessage()
    {
        var (client, _) = CreateClient(
            HttpStatusCode.BadRequest,
            """{"code":400,"error_code":"invalid_credentials","msg":"Invalid login credentials"}""");
        using var __ = client;

        var error = await Assert.ThrowsAsync<AuthException>(
            () => client.SignInAsync("anna@example.ru", "wrong"));

        Assert.Equal("Неверный email или пароль.", error.Message);
        Assert.Equal("invalid_credentials", error.Code);
    }

    [Fact]
    public async Task SignIn_UnconfirmedEmail_ExplainsWhatToDo()
    {
        var (client, _) = CreateClient(
            HttpStatusCode.BadRequest,
            """{"error_code":"email_not_confirmed","msg":"Email not confirmed"}""");
        using var __ = client;

        var error = await Assert.ThrowsAsync<AuthException>(
            () => client.SignInAsync("anna@example.ru", "secret"));

        Assert.Contains("не подтверждён", error.Message);
        Assert.Equal("email_not_confirmed", error.Code);
    }

    [Fact]
    public async Task SignIn_TooManyAttempts_AsksToWait()
    {
        var (client, _) = CreateClient(
            HttpStatusCode.TooManyRequests,
            """{"error_code":"over_request_rate_limit","msg":"Too many requests"}""");
        using var __ = client;

        var error = await Assert.ThrowsAsync<AuthException>(
            () => client.SignInAsync("anna@example.ru", "secret"));

        Assert.Contains("Слишком много попыток", error.Message);
    }

    [Fact]
    public async Task SignIn_RejectsEmptyCredentialsWithoutNetwork()
    {
        var (client, handler) = CreateClient(HttpStatusCode.OK, SessionJson());
        using var _ = client;

        await Assert.ThrowsAsync<ArgumentException>(() => client.SignInAsync("", "secret"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.SignInAsync("anna@example.ru", "  "));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SignIn_IncompleteSession_Throws()
    {
        var (client, _) = CreateClient(HttpStatusCode.OK, """{"access_token":"a"}""");
        using var __ = client;

        await Assert.ThrowsAsync<AuthException>(() => client.SignInAsync("anna@example.ru", "secret"));
    }

    [Fact]
    public async Task Refresh_SendsRefreshGrantAndReturnsNewSession()
    {
        var (client, handler) = CreateClient(HttpStatusCode.OK, SessionJson());
        using var _ = client;

        var session = await client.RefreshAsync("refresh-1");

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://example.supabase.co/auth/v1/token?grant_type=refresh_token", request.Url);
        Assert.Contains("\"refresh_token\":\"refresh-1\"", request.Body);
        Assert.Equal("access-1", session.AccessToken);
    }

    [Fact]
    public async Task Refresh_ExpiredToken_AsksToSignInAgain()
    {
        var (client, _) = CreateClient(
            HttpStatusCode.BadRequest,
            """{"error_code":"refresh_token_not_found","msg":"Invalid Refresh Token"}""");
        using var __ = client;

        var error = await Assert.ThrowsAsync<AuthException>(() => client.RefreshAsync("stale"));

        Assert.Equal("Сессия истекла. Войдите заново.", error.Message);
    }

    [Fact]
    public void Session_IsExpiredOneMinuteBeforeDeadline()
    {
        var now = new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
        var session = new AuthSession
        {
            AccessToken = "a",
            RefreshToken = "r",
            UserId = UserId,
            Email = "anna@example.ru",
            ExpiresAt = now.AddMinutes(5),
        };

        Assert.False(session.IsExpired(now));
        Assert.False(session.IsExpired(now.AddMinutes(3)));
        Assert.True(session.IsExpired(now.AddMinutes(4)));
        Assert.True(session.IsExpired(now.AddMinutes(10)));
    }

    private sealed record CapturedRequest(HttpMethod Method, string Url, string? ApiKey, string Body);

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public List<CapturedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var apiKey = request.Headers.TryGetValues("apikey", out var values)
                ? values.FirstOrDefault()
                : null;

            Requests.Add(new CapturedRequest(request.Method, request.RequestUri!.ToString(), apiKey, body));

            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
