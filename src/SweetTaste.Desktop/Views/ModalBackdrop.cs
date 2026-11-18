using System;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;

namespace SweetTaste.Desktop.Views;

/// <summary>
/// Закрывает модальные окна только для просмотра по клику на затемнённый фон.
/// </summary>
internal static class ModalBackdrop
{
    public static void CloseOnClick(Control backdrop, Func<object?, ICommand?> resolveCommand)
    {
        backdrop.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(backdrop).Properties.IsLeftButtonPressed)
            {
                return;
            }

            // Команда берётся в момент клика: DataContext задаётся позже конструктора окна.
            var command = resolveCommand(backdrop.DataContext);
            if (command?.CanExecute(null) != true)
            {
                return;
            }

            command.Execute(null);
            e.Handled = true;
        };
    }
}
