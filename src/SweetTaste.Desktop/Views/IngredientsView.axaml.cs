using Avalonia.Controls;
using SweetTaste.Desktop.ViewModels;

namespace SweetTaste.Desktop.Views;

public partial class IngredientsView : UserControl
{
    public IngredientsView()
    {
        InitializeComponent();
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
