using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SweetTaste.Desktop.ViewModels;

namespace SweetTaste.Desktop.Views;

public partial class ReportsView : UserControl
{
    public ReportsView()
    {
        InitializeComponent();
    }

    private async void OnExportCsv(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ReportsViewModel viewModel)
        {
            return;
        }

        var text = viewModel.BuildCsvReport();
        if (text.Length == 0)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return;
        }

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Экспорт отчёта в Excel",
            SuggestedFileName = $"sweet-taste-report-{DateTime.Now:yyyy-MM-dd}.csv",
            DefaultExtension = "csv",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("CSV для Excel")
                {
                    Patterns = new[] { "*.csv" },
                },
            },
        });

        if (file is null)
        {
            return;
        }

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await writer.WriteAsync(text);
    }

    private async void OnExportPdf(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ReportsViewModel viewModel)
        {
            return;
        }

        var html = viewModel.BuildHtmlReport();
        if (html.Length == 0)
        {
            return;
        }

        try
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                $"sweet-taste-report-{DateTime.Now:yyyyMMdd-HHmmss}.html");

            await File.WriteAllTextAsync(path, html, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Если браузер не открылся, пользователь может повторить экспорт.
        }
    }
}
