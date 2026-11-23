using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SweetTaste.Core.Services;
using SweetTaste.Core.Storage;
using SweetTaste.Core.Sync;

namespace SweetTaste.Desktop.Sync;

/// <summary>
/// Выгружает снапшот в облако при закрытии приложения.
/// </summary>
/// <remarks>
/// Закрытие окна не должно ждать сеть дольше нескольких десятков секунд, поэтому
/// выгрузка ограничена таймаутом и любые ошибки только пишутся в журнал: локальная
/// база остаётся источником истины, следующая выгрузка повторит попытку.
/// </remarks>
internal static class SyncOnExit
{
    // Один запрос до Supabase занимает 0.3–2.4 с, а снапшот уходит одним RPC
    // с base64-фото рецептов: 5 с не хватало даже на пустую базу. Запросов
    // теперь два (архив и замена), поэтому таймаут поднят с 30 до 45 с.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

    public static void Run(
        AppDatabase database,
        SupabaseAuthClient auth,
        SyncService sync,
        ISessionStore sessions,
        SettingsService settings)
    {
        try
        {
            if (!settings.UploadOnExit)
            {
                Log("выгрузка при закрытии отключена в настройках");
                return;
            }

            var session = sessions.Load();
            if (session is null)
            {
                Log("сессии нет — выгрузка пропущена");
                return;
            }

            Log($"сессия загружена: {session.Email}, истекает {session.ExpiresAt:yyyy-MM-dd HH:mm:ss}");

            // Ждём выгрузку синхронно: после ShutdownRequested процесс завершится,
            // и фоновая задача не успеет дописать данные. Работа идёт в пуле потоков:
            // продолжения await внутри UploadAsync не должны возвращаться в UI-поток,
            // который здесь заблокирован, иначе закрытие зависает навсегда.
            Task.Run(() => UploadAsync(database, auth, sync, sessions, settings, session))
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception exception)
        {
            // Причина архивации спрятана во вложенном исключении: снаружи только
            // «выгрузка отменена», а в журнале нужен ответ сервера.
            Log(exception.InnerException is null
                ? $"{exception.GetType().Name}: {exception.Message}"
                : $"{exception.GetType().Name}: {exception.Message} Причина: {exception.InnerException.Message}");
        }
    }

    private static async Task UploadAsync(
        AppDatabase database,
        SupabaseAuthClient auth,
        SyncService sync,
        ISessionStore sessions,
        SettingsService settings,
        AuthSession session)
    {
        using var cancellation = new CancellationTokenSource(Timeout);

        if (session.IsExpired(DateTimeOffset.UtcNow))
        {
            Log("токен истёк — обновляю");
            session = await auth.RefreshAsync(session.RefreshToken, cancellation.Token);
            sessions.Save(session);
            Log("токен обновлён");
        }

        var snapshot = new SnapshotExporter(database).Export(DateTimeOffset.UtcNow);
        Log($"снапшот собран: {snapshot.TotalRows} строк");

        // Пустую базу не выгружаем, даже не спрашивая облако: она стёрла бы его целиком.
        var cloud = snapshot.TotalRows == 0
            ? null
            : await sync.ReadCloudStateAsync(session, cancellation.Token);

        var skipReason = ExitUploadGuard.Check(snapshot.TotalRows, cloud, settings.CloudSnapshotSeenAt);
        if (skipReason is not null)
        {
            settings.ExitUploadSkipReason = skipReason;
            Log("выгрузка пропущена: " + skipReason);
            return;
        }

        var result = await sync.UploadWithArchiveAsync(
            snapshot,
            session,
            cancellationToken: cancellation.Token);

        settings.CloudSnapshotSeenAt = snapshot.TakenAt;
        settings.ExitUploadSkipReason = string.Empty;

        Log(result.Archive.Archived
            ? $"архив: сохранён ({result.Archive.TotalRows} строк)"
            : $"архив: пропущен ({result.Archive.Reason ?? "причина не указана"})");

        Log($"выгрузка завершена: {result.Upload.TotalRows} строк");
    }

    private static void Log(string message)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SweetTaste");
            Directory.CreateDirectory(directory);

            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}";
            File.AppendAllText(Path.Combine(directory, "sync.log"), line + Environment.NewLine);
        }
        catch (IOException)
        {
            // Журнал — вспомогательный: если его не удалось записать, закрытие всё равно продолжается.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
