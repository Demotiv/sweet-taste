using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SweetTaste.Desktop.ViewModels;

namespace SweetTaste.Desktop.Views;

public partial class OrdersView : UserControl
{
    public OrdersView()
    {
        InitializeComponent();
    }

    private async void OnCopyShopping(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not OrdersViewModel viewModel)
        {
            return;
        }

        var text = viewModel.BuildShoppingText();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            viewModel.SetShoppingNotice("Буфер обмена недоступен.");
            return;
        }

        try
        {
            await clipboard.SetTextAsync(text);
            viewModel.SetShoppingNotice("Список покупок скопирован.");
        }
        catch (Exception)
        {
            viewModel.SetShoppingNotice("Не удалось скопировать список.");
        }
    }

    private void OnPrintShopping(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not OrdersViewModel viewModel)
        {
            return;
        }

        var text = viewModel.BuildShoppingText();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                $"sweet-taste-shopping-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

            File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            viewModel.SetShoppingNotice("Список открыт для печати.");
        }
        catch (Exception)
        {
            viewModel.SetShoppingNotice("Не удалось открыть список для печати.");
        }
    }
}
