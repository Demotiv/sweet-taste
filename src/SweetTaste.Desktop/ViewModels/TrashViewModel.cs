using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SweetTaste.Core.Domain;
using SweetTaste.Core.Services;

namespace SweetTaste.Desktop.ViewModels;

/// <summary>Корзина: удалённые ингредиенты и рецепты можно вернуть.</summary>
public sealed partial class TrashViewModel : ViewModelBase, IFormPage
{
    private readonly InventoryService _inventory;
    private readonly RecipeService _recipes;
    private Action? _confirmAction;

    public TrashViewModel(InventoryService inventory, RecipeService recipes)
    {
        _inventory = inventory;
        _recipes = recipes;
        Reload();
    }

    public ObservableCollection<TrashIngredientRowViewModel> Ingredients { get; } = new();

    public ObservableCollection<TrashRecipeRowViewModel> Recipes { get; } = new();

    [ObservableProperty]
    private bool _isConfirmOpen;

    [ObservableProperty]
    private string _confirmText = string.Empty;

    [ObservableProperty]
    private string _confirmButtonText = "Удалить навсегда";

    [ObservableProperty]
    private string? _error;

    public bool IsEmptyIngredients => Ingredients.Count == 0;

    public bool IsEmptyRecipes => Recipes.Count == 0;

    public string Subtitle
    {
        get
        {
            if (Ingredients.Count == 0 && Recipes.Count == 0)
            {
                return "Корзина пуста";
            }

            return $"{Ingredients.Count} {Formats.Plural(Ingredients.Count, "ингредиент", "ингредиента", "ингредиентов")} · " +
                   $"{Recipes.Count} {Formats.Plural(Recipes.Count, "рецепт", "рецепта", "рецептов")}";
        }
    }

    public bool IsFormOpen => IsConfirmOpen;

    public void CancelForm()
    {
        if (IsConfirmOpen)
        {
            IsConfirmOpen = false;
            _confirmAction = null;
        }
    }

    public void RestoreIngredient(TrashIngredientRowViewModel row)
    {
        try
        {
            _inventory.RestoreIngredient(row.Id);
            Error = null;
            Reload();
        }
        catch (ValidationException ex)
        {
            Error = ex.Message;
        }
    }

    public void RestoreRecipe(TrashRecipeRowViewModel row)
    {
        try
        {
            _recipes.RestoreRecipe(row.Id);
            Error = null;
            Reload();
        }
        catch (ValidationException ex)
        {
            Error = ex.Message;
        }
    }

    public void RequestDeleteIngredient(TrashIngredientRowViewModel row)
    {
        _confirmAction = () =>
        {
            try
            {
                _inventory.DeleteIngredientPermanently(row.Id);
                Error = null;
                Reload();
            }
            catch (ValidationException ex)
            {
                Error = ex.Message;
            }
        };

        ConfirmText = $"Удалить ингредиент «{row.Name}» навсегда? История закупок и строки в рецептах тоже удалятся, восстановить будет нельзя.";
        ConfirmButtonText = "Удалить навсегда";
        IsConfirmOpen = true;
    }

    public void RequestDeleteRecipe(TrashRecipeRowViewModel row)
    {
        _confirmAction = () =>
        {
            try
            {
                _recipes.DeleteRecipePermanently(row.Id);
                Error = null;
                Reload();
            }
            catch (ValidationException ex)
            {
                Error = ex.Message;
            }
        };

        ConfirmText = $"Удалить рецепт «{row.Name}» навсегда? Восстановить будет нельзя.";
        ConfirmButtonText = "Удалить навсегда";
        IsConfirmOpen = true;
    }

    [RelayCommand]
    private void ConfirmDelete()
    {
        IsConfirmOpen = false;
        _confirmAction?.Invoke();
        _confirmAction = null;
    }

    [RelayCommand]
    private void CancelConfirm()
    {
        IsConfirmOpen = false;
        _confirmAction = null;
    }

    private void Reload()
    {
        Ingredients.Clear();
        foreach (var item in _inventory.GetDeletedIngredients())
        {
            Ingredients.Add(new TrashIngredientRowViewModel(item, this));
        }

        Recipes.Clear();
        foreach (var item in _recipes.GetDeletedRecipes())
        {
            Recipes.Add(new TrashRecipeRowViewModel(item, this));
        }

        OnPropertyChanged(nameof(IsEmptyIngredients));
        OnPropertyChanged(nameof(IsEmptyRecipes));
        OnPropertyChanged(nameof(Subtitle));
    }
}

/// <summary>Удалённый ингредиент в корзине.</summary>
public sealed class TrashIngredientRowViewModel : ViewModelBase
{
    public TrashIngredientRowViewModel(DeletedIngredient item, TrashViewModel owner)
    {
        Id = item.Ingredient.Id;
        Name = item.Ingredient.Name;
        CategoryText = item.CategoryName ?? "Без категории";
        DeletedText = "Удалён " + Formats.Moment(item.DeletedAt);
        DetailText = Formats.Purchases(item.PurchaseCount);
        RestoreCommand = new RelayCommand(() => owner.RestoreIngredient(this));
        DeleteCommand = new RelayCommand(() => owner.RequestDeleteIngredient(this));
    }

    public Guid Id { get; }

    public string Name { get; }

    public string CategoryText { get; }

    public string DeletedText { get; }

    public string DetailText { get; }

    public IRelayCommand RestoreCommand { get; }

    public IRelayCommand DeleteCommand { get; }
}

/// <summary>Удалённый рецепт в корзине.</summary>
public sealed class TrashRecipeRowViewModel : ViewModelBase
{
    public TrashRecipeRowViewModel(DeletedRecipe item, TrashViewModel owner)
    {
        Id = item.Recipe.Id;
        Name = item.Recipe.Name;
        CategoryText = item.CategoryName ?? "Без категории";
        DeletedText = "Удалён " + Formats.Moment(item.DeletedAt);
        DetailText = Formats.Output(item.Recipe.OutputQuantity, item.Recipe.OutputUnit);
        RestoreCommand = new RelayCommand(() => owner.RestoreRecipe(this));
        DeleteCommand = new RelayCommand(() => owner.RequestDeleteRecipe(this));
    }

    public Guid Id { get; }

    public string Name { get; }

    public string CategoryText { get; }

    public string DeletedText { get; }

    public string DetailText { get; }

    public IRelayCommand RestoreCommand { get; }

    public IRelayCommand DeleteCommand { get; }
}
