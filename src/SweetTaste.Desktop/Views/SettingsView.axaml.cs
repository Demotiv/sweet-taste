using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SweetTaste.Desktop.ViewModels;

namespace SweetTaste.Desktop.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private async void OnRestoreClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Выберите файл базы для восстановления",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("База SQLite")
                {
                    Patterns = new[] { "*.db", "*.sqlite", "*.sqlite3" },
                },
            },
        });

        if (files.Count == 0)
        {
            return;
        }

        var uri = files[0].Path;
        if (!uri.IsFile)
        {
            viewModel.SetRestoreError("Выберите файл на диске или флешке.");
            return;
        }

        viewModel.RequestRestore(uri.LocalPath);
    }
}
