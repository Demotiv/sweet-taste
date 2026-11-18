using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using SweetTaste.Desktop.ViewModels;

namespace SweetTaste.Desktop.Views;

public partial class IngredientsView : UserControl
{
    public IngredientsView()
    {
        InitializeComponent();

        ModalBackdrop.CloseOnClick(
            IngredientDetailsBackdrop,
            data => (data as IngredientsViewModel)?.CloseDetailsCommand);
        ModalBackdrop.CloseOnClick(
            PackagingBackdrop,
            data => (data as IngredientsViewModel)?.ClosePackagingCommand);
    }

    private void OnIngredientRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        // Кнопки в строке (цена, изменить, удалить) обрабатывают нажатие сами.
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            return;
        }

        if (sender is Control { DataContext: IngredientRowViewModel row } &&
            DataContext is IngredientsViewModel viewModel)
        {
            viewModel.OpenDetails(row);
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
