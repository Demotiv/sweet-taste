using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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
    // с base64-фото рецептов: 5 с не хватало даже на пустую базу.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public static void Run(
        AppDatabase database,
        SupabaseAuthClient auth,
        SyncService sync,
        ISessionStore sessions)
    {
        try
        {
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
            Task.Run(() => UploadAsync(database, auth, sync, sessions, session))
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception exception)
        {
            Log($"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private static async Task UploadAsync(
        AppDatabase database,
        SupabaseAuthClient auth,
        SyncService sync,
        ISessionStore sessions,
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

        var result = await sync.UploadAtomicAsync(snapshot, session, cancellation.Token);
        Log($"выгрузка завершена: {result.TotalRows} строк");
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
