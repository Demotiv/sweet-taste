using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using SweetTaste.Desktop.ViewModels;

namespace SweetTaste.Desktop.Views;

public partial class RecipesView : UserControl
{
    public RecipesView()
    {
        InitializeComponent();

        ModalBackdrop.CloseOnClick(PreviewBackdrop, data => (data as RecipesViewModel)?.ClosePreviewCommand);
    }

    private async void OnPickRecipePhoto(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RecipesViewModel viewModel)
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
            Title = "Фото рецепта",
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

    private void OnRecipeRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        // Кнопки в строке (изменить, удалить) обрабатывают нажатие сами.
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            return;
        }

        if (sender is Control { DataContext: RecipeRowViewModel row } &&
            DataContext is RecipesViewModel viewModel)
        {
            viewModel.OpenPreview(row);
            e.Handled = true;
        }
    }

    private void IngredientCombo_ContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container is not ComboBoxItem item || item.DataContext is not IngredientPickItem pick)
        {
            return;
        }

        item.IsEnabled = !pick.IsHeader;

        if (pick.IsHeader)
        {
            item.Classes.Add("header");
        }
        else
        {
            item.Classes.Remove("header");
        }
    }
}
