using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using SweetTaste.Core.Updates;

namespace SweetTaste.Desktop.Updates;

/// <summary>Найденный на GitHub релиз.</summary>
public sealed record AppUpdate(
    UpdateVersion Version,
    string Tag,
    string ZipUrl,
    string ShaUrl,
    string PageUrl);

/// <summary>Скачанный и проверенный архив обновления.</summary>
public sealed record DownloadedUpdate(string ZipPath, string Sha256);

/// <summary>
/// Проверяет релизы Sweet Taste на GitHub, скачивает ZIP и запускает обновление.
/// Публичный репозиторий не требует токена: достаточно стандартного GitHub API.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private const string Repository = "Demotiv/sweet-taste";
    private const string ReleasesUrl = $"https://api.github.com/repos/{Repository}/releases?per_page=20";
    private const string ReleasesAtomUrl = $"https://github.com/{Repository}/releases.atom";

    private readonly HttpClient _http;

    public UpdateService()
    {
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(15),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SweetTaste-UpdateCheck/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public string AppDirectory =>
        (Environment.ProcessPath is { } path ? Path.GetDirectoryName(path) : AppContext.BaseDirectory)
        ?? AppContext.BaseDirectory;

    public bool CanSelfUpdate =>
        File.Exists(Path.Combine(AppDirectory, "AutoUpdate.ps1")) &&
        File.Exists(Path.Combine(AppDirectory, "SweetTaste.exe"));

    public async Task<AppUpdate?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        // Atom-лента не расходует лимит GitHub API. Если она недоступна,
        // используем официальный API как запасной вариант.
        try
        {
            var fromAtom = await GetLatestFromAtomAsync(cancellationToken);
            if (fromAtom is not null)
            {
                return fromAtom;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Переходим к API.
        }

        return await GetLatestFromApiAsync(cancellationToken);
    }

    private async Task<AppUpdate?> GetLatestFromAtomAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        var xml = await _http.GetStringAsync(ReleasesAtomUrl, timeout.Token);
        var document = XDocument.Parse(xml);
        XNamespace atom = "http://www.w3.org/2005/Atom";

        AppUpdate? latest = null;

        foreach (var entry in document.Root?.Elements(atom + "entry") ?? Enumerable.Empty<XElement>())
        {
            var pageUrl = entry.Elements(atom + "link")
                .FirstOrDefault(link => string.Equals(
                    (string?)link.Attribute("rel"), "alternate", StringComparison.OrdinalIgnoreCase))?
                .Attribute("href")?
                .Value;

            if (string.IsNullOrWhiteSpace(pageUrl))
            {
                continue;
            }

            var tag = pageUrl.TrimEnd('/').Split('/').LastOrDefault() ?? string.Empty;
            if (!UpdateVersion.TryParse(tag, out var version))
            {
                continue;
            }

            if (latest is not null && version.CompareTo(latest.Version) <= 0)
            {
                continue;
            }

            var versionText = version.ToString();
            var assetRoot = $"https://github.com/{Repository}/releases/download/{tag}/";
            latest = new AppUpdate(
                version,
                tag,
                assetRoot + $"SweetTaste-{versionText}-win-x64.zip",
                assetRoot + $"SweetTaste-{versionText}-win-x64.zip.sha256",
                pageUrl);
        }

        return latest;
    }

    private async Task<AppUpdate?> GetLatestFromApiAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        var json = await _http.GetStringAsync(ReleasesUrl, timeout.Token);
        using var document = JsonDocument.Parse(json);

        AppUpdate? latest = null;

        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean())
            {
                continue;
            }

            if (!release.TryGetProperty("tag_name", out var tagElement) ||
                !UpdateVersion.TryParse(tagElement.GetString(), out var version))
            {
                continue;
            }

            if (latest is not null && version.CompareTo(latest.Version) <= 0)
            {
                continue;
            }

            if (!release.TryGetProperty("assets", out var assets))
            {
                continue;
            }

            string? zipUrl = null;
            string? shaUrl = null;

            foreach (var asset in assets.EnumerateArray())
            {
                if (!asset.TryGetProperty("name", out var nameElement) ||
                    !asset.TryGetProperty("browser_download_url", out var urlElement))
                {
                    continue;
                }

                var name = nameElement.GetString() ?? string.Empty;
                var url = urlElement.GetString() ?? string.Empty;

                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                    name.StartsWith("SweetTaste-", StringComparison.OrdinalIgnoreCase))
                {
                    zipUrl = url;
                }
                else if (name.EndsWith(".zip.sha256", StringComparison.OrdinalIgnoreCase))
                {
                    shaUrl = url;
                }
            }

            if (zipUrl is null || shaUrl is null)
            {
                continue;
            }

            var pageUrl = release.TryGetProperty("html_url", out var pageElement)
                ? pageElement.GetString() ?? string.Empty
                : string.Empty;

            latest = new AppUpdate(
                version,
                tagElement.GetString() ?? version.ToString(),
                zipUrl,
                shaUrl,
                pageUrl);
        }

        return latest;
    }

    public async Task<DownloadedUpdate> DownloadAsync(
        AppUpdate update,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));

        var shaText = await _http.GetStringAsync(update.ShaUrl, timeout.Token);
        var expected = shaText
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?
            .Trim()
            .ToLowerInvariant();

        if (expected is not { Length: 64 } || !expected.All(IsHex))
        {
            throw new InvalidOperationException("В файле SHA-256 нет корректного хеша.");
        }

        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SweetTaste",
            "updates");
        Directory.CreateDirectory(directory);

        var fileName = Path.GetFileName(new Uri(update.ZipUrl).LocalPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = $"SweetTaste-{update.Version}-win-x64.zip";
        }

        var path = Path.Combine(directory, fileName);

        using var response = await _http.GetAsync(
            update.ZipUrl,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;

        await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
        await using (var output = new FileStream(
                         path,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         bufferSize: 81920,
                         useAsync: true))
        {
            var buffer = new byte[81920];
            long received = 0;

            while (true)
            {
                var read = await input.ReadAsync(buffer, timeout.Token);
                if (read == 0)
                {
                    break;
                }

                await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                received += read;

                if (total is > 0)
                {
                    progress?.Report((double)received / total.Value);
                }
            }
        }

        await using (var file = File.OpenRead(path))
        {
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(file, timeout.Token))
                .ToLowerInvariant();

            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                File.Delete(path);
                throw new InvalidOperationException("SHA-256 скачанного архива не совпадает с файлом релиза.");
            }
        }

        progress?.Report(1d);
        return new DownloadedUpdate(path, expected);
    }

    public void StartUpdater(string databasePath, DownloadedUpdate download, AppUpdate update)
    {
        if (!CanSelfUpdate)
        {
            throw new InvalidOperationException(
                "Автообновление доступно только в собранной версии приложения.");
        }

        var script = Path.Combine(AppDirectory, "AutoUpdate.ps1");
        var temporaryScript = Path.Combine(
            Path.GetTempPath(),
            $"sweet-taste-update-{Guid.NewGuid():N}.ps1");
        File.Copy(script, temporaryScript, overwrite: true);

        var process = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetTempPath(),
        };

        process.ArgumentList.Add("-NoProfile");
        process.ArgumentList.Add("-ExecutionPolicy");
        process.ArgumentList.Add("Bypass");
        process.ArgumentList.Add("-File");
        process.ArgumentList.Add(temporaryScript);
        process.ArgumentList.Add("-AppDir");
        process.ArgumentList.Add(AppDirectory);
        process.ArgumentList.Add("-ZipPath");
        process.ArgumentList.Add(download.ZipPath);
        process.ArgumentList.Add("-ParentPid");
        process.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        process.ArgumentList.Add("-ExpectedVersion");
        process.ArgumentList.Add(update.Version.ToString());
        process.ArgumentList.Add("-ExpectedSha256");
        process.ArgumentList.Add(download.Sha256);
        process.ArgumentList.Add("-DatabasePath");
        process.ArgumentList.Add(databasePath);

        Process.Start(process);
    }

    public void Dispose() => _http.Dispose();

    private static bool IsHex(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f';
}
