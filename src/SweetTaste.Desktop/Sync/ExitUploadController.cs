using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using SweetTaste.Core.Services;
using SweetTaste.Core.Storage;
using SweetTaste.Core.Sync;
using SweetTaste.Desktop.ViewModels;

namespace SweetTaste.Desktop.Sync;

/// <summary>
/// Выгрузка в облако при закрытии окна без зависания интерфейса.
/// </summary>
/// <remarks>
/// Раньше выгрузка шла в <c>ShutdownRequested</c> и блокировала UI-поток до 45 с:
/// окно не перерисовывалось, и Windows помечала его «Не отвечает». Теперь первое
/// закрытие окна отменяется, поверх окна показывается «Сохраняю в облако», выгрузка
/// идёт в фоне, а по её окончании окно закрывается само. Кнопка «Закрыть без
/// выгрузки» прерывает её.
/// <para>
/// Выключение Windows и программное завершение (обновление, перезапуск) окно
/// не откладывает: для них остаётся блокирующий <see cref="SyncOnExit.Run"/> из
/// <see cref="RunBlockingIfPending"/>. Флаг <c>_done</c> не даёт выгрузить дважды.
/// </para>
/// </remarks>
internal sealed class ExitUploadController
{
    private readonly AppDatabase _database;
    private readonly SupabaseAuthClient _auth;
    private readonly SyncService _sync;
    private readonly ISessionStore _sessions;
    private readonly SettingsService _settings;
    private readonly MainWindowViewModel _viewModel;

    private bool _done;
    private CancellationTokenSource? _cancellation;

    public ExitUploadController(
        AppDatabase database,
        SupabaseAuthClient auth,
        SyncService sync,
        ISessionStore sessions,
        SettingsService settings,
        MainWindowViewModel viewModel)
    {
        _database = database;
        _auth = auth;
        _sync = sync;
        _sessions = sessions;
        _settings = settings;
        _viewModel = viewModel;
        _viewModel.ExitUploadSkipRequested += () => _cancellation?.Cancel();
    }

    /// <summary>Обработчик <see cref="Window.Closing"/> главного окна.</summary>
    public void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_done || sender is not Window window)
        {
            return;
        }

        // Систему и программное завершение не задерживаем — их обслужит RunBlockingIfPending.
        if (e.CloseReason is WindowCloseReason.OSShutdown or WindowCloseReason.ApplicationShutdown)
        {
            return;
        }

        // Повторный крестик, пока идёт выгрузка: окно закроется само.
        if (_cancellation is not null)
        {
            e.Cancel = true;
            return;
        }

        if (!SyncOnExit.IsNeeded(_settings, _sessions))
        {
            _done = true;
            return;
        }

        e.Cancel = true;
        _ = UploadThenCloseAsync(window);
    }

    /// <summary>Обработчик <c>ShutdownRequested</c>: блокирующая выгрузка, если её ещё не было.</summary>
    public void RunBlockingIfPending()
    {
        if (_done || _cancellation is not null)
        {
            return;
        }

        _done = true;
        SyncOnExit.Run(_database, _auth, _sync, _sessions, _settings);
    }

    private async Task UploadThenCloseAsync(Window window)
    {
        _cancellation = new CancellationTokenSource();
        _viewModel.IsExitUploadActive = true;

        try
        {
            // В пуле потоков: снапшот читается из SQLite синхронно и не должен тормозить окно.
            var token = _cancellation.Token;
            await Task.Run(() => SyncOnExit.RunAsync(_database, _auth, _sync, _sessions, _settings, token));
        }
        finally
        {
            _done = true;
            window.Close();
        }
    }
}
