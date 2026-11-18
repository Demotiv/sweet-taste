using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SweetTaste.Desktop.ViewModels;

namespace SweetTaste.Desktop.Views;

public partial class ProfileView : UserControl
{
    public ProfileView()
    {
        InitializeComponent();
    }

    private async void OnPickPhoto(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ProfileViewModel viewModel)
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
            Title = "Выберите фото или логотип",
            AllowMultiple = false,
            FileTypeFilter = new[] { FilePickerFileTypes.ImageAll },
        });

        if (files.Count == 0)
        {
            return;
        }

        await using var stream = await files[0].OpenReadAsync();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        viewModel.SetPhoto(buffer.ToArray());
    }

    private async void OnExportData(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ProfileViewModel viewModel)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return;
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Куда сохранить данные",
            AllowMultiple = false,
        });

        if (folders.Count == 0)
        {
            return;
        }

        var uri = folders[0].Path;
        if (!uri.IsFile)
        {
            viewModel.SetExportError("Выберите папку на диске или флешке.");
            return;
        }

        viewModel.ExportData(uri.LocalPath);
    }
}
