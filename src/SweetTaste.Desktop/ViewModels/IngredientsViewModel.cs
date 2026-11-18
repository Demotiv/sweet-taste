using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SweetTaste.Core.Domain;
using SweetTaste.Core.Services;

namespace SweetTaste.Desktop.ViewModels;

/// <summary>Справочник ингредиентов и журнал закупок — первый рабочий раздел.</summary>
public sealed partial class IngredientsViewModel : ViewModelBase, IFormPage
{
    private const string NoCategory = "Без категории";

    private readonly InventoryService _service;
    private readonly PackagingService _packaging;
    private List<IngredientSummary> _summaries = new();
    private readonly HashSet<string> _expandedGroups = new();
    private readonly HashSet<string> _expandedPackagingGroups = new();
    private Guid? _editingIngredientId;
    private Guid? _detailsIngredientId;
    private Guid? _editingPurchaseId;
    private Guid? _editingPackagingId;
    private Action? _confirmAction;
    private IngredientGroupViewModel? _renamingGroup;

    public IngredientsViewModel(InventoryService service, PackagingService packaging)
    {
        _service = service;
        _packaging = packaging;
        ReloadOptions();
        Reload();
    }

    public ObservableCollection<IngredientGroupViewModel> Groups { get; } = new();

    public ObservableCollection<IngredientPickItem> IngredientOptions { get; } = new();

    public ObservableCollection<PurchaseRowViewModel> DetailsPurchases { get; } = new();

    public ObservableCollection<string> CategoryOptions { get; } = new();

    public ObservableCollection<PackagingRowViewModel> PackagingRows { get; } = new();

    public ObservableCollection<PackagingGroupViewModel> PackagingGroups { get; } = new();

    public ObservableCollection<string> PackagingCategoryOptions { get; } = new();

    public IReadOnlyList<string> UnitsList => Units.Allowed;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _subtitle = string.Empty;

    [ObservableProperty]
    private string? _pageError;

    [ObservableProperty]
    private string _spentThisMonthText = "0 ₽";

    [ObservableProperty]
    private string _purchasesThisMonthText = "0";

    [ObservableProperty]
    private string _largestPurchaseValue = "—";

    [ObservableProperty]
    private string _largestPurchaseNote = "В этом месяце закупок не было";

    // --- форма ингредиента ---
    [ObservableProperty]
    private bool _isIngredientFormOpen;

    [ObservableProperty]
    private string _ingredientFormTitle = "Новый ингредиент";

    [ObservableProperty]
    private string _formName = string.Empty;

    [ObservableProperty]
    private string _formBrand = string.Empty;

    [ObservableProperty]
    private string _formCategorySelection = NoCategory;

    [ObservableProperty]
    private string _formNewCategory = string.Empty;

    [ObservableProperty]
    private string _formUnit = Units.Grams;

    [ObservableProperty]
    private bool _isUnitLocked;

    [ObservableProperty]
    private string? _ingredientFormError;

    // --- форма закупки ---
    [ObservableProperty]
    private bool _isPurchaseFormOpen;

    [ObservableProperty]
    private string _purchaseFormTitle = "Новая закупка";

    [ObservableProperty]
    private bool _isPurchaseEditing;

    [ObservableProperty]
    private IngredientPickItem? _purchaseIngredient;

    [ObservableProperty]
    private DateTimeOffset? _purchaseDate = DateTimeOffset.Now;

    [ObservableProperty]
    private string _purchaseStore = string.Empty;

    [ObservableProperty]
    private decimal? _purchaseQuantity;

    [ObservableProperty]
    private decimal? _purchaseTotal;

    [ObservableProperty]
    private string _purchaseNote = string.Empty;

    [ObservableProperty]
    private string _purchaseUnit = Units.Grams;

    [ObservableProperty]
    private string _purchasePreview = "—";

    [ObservableProperty]
    private string? _purchaseFormError;

    // --- история ---
    [ObservableProperty]
    private bool _isDetailsOpen;

    [ObservableProperty]
    private string _detailsTitle = string.Empty;

    [ObservableProperty]
    private string _detailsBrand = string.Empty;

    [ObservableProperty]
    private string _detailsCategory = string.Empty;

    [ObservableProperty]
    private bool _detailsHasCategory;

    [ObservableProperty]
    private string _detailsAverage = "—";

    [ObservableProperty]
    private string _detailsTotalSpent = "0 ₽";

    [ObservableProperty]
    private string _detailsTotalQuantity = "0";

    [ObservableProperty]
    private string? _detailsError;

    // --- упаковка ---
    [ObservableProperty]
    private bool _isPackagingOpen;

    [ObservableProperty]
    private string _packagingSummary = string.Empty;

    [ObservableProperty]
    private string _packagingCategoryName = string.Empty;

    [ObservableProperty]
    private string? _packagingError;

    [ObservableProperty]
    private bool _isPackagingFormOpen;

    [ObservableProperty]
    private string _packagingFormTitle = "Новая упаковка";

    [ObservableProperty]
    private string _packagingFormName = string.Empty;

    [ObservableProperty]
    private string _packagingFormSize = string.Empty;

    [ObservableProperty]
    private decimal? _packagingFormPrice = 100m;

    [ObservableProperty]
    private string _packagingFormCategorySelection = NoCategory;

    [ObservableProperty]
    private string? _packagingFormError;

    // --- подтверждение удаления ---
    [ObservableProperty]
    private bool _isConfirmOpen;

    [ObservableProperty]
    private string _confirmText = string.Empty;

    [ObservableProperty]
    private string _confirmButtonText = "Удалить";

    // --- переименование категории ---
    [ObservableProperty]
    private bool _isCategoryRenameOpen;

    [ObservableProperty]
    private string _categoryRenameValue = string.Empty;

    [ObservableProperty]
    private string? _categoryRenameError;

    public bool IsEmpty => Groups.Count == 0;

    public string EmptyText => _summaries.Count == 0
        ? "Пока нет ингредиентов — добавьте первый."
        : "Ничего не найдено. Попробуйте другой запрос.";

    public bool HasIngredients => _summaries.Count > 0;

    public bool DetailsEmpty => DetailsPurchases.Count == 0;

    public bool IsPackagingEmpty => PackagingGroups.Count == 0;

    public bool IsFormOpen => IsIngredientFormOpen || IsPurchaseFormOpen || IsDetailsOpen ||
                              IsConfirmOpen || IsPackagingOpen || IsPackagingFormOpen ||
                              IsCategoryRenameOpen;

    public void CancelForm()
    {
        if (IsConfirmOpen)
        {
            IsConfirmOpen = false;
            _confirmAction = null;
        }
        else if (IsCategoryRenameOpen)
        {
            IsCategoryRenameOpen = false;
        }
        else if (IsPackagingFormOpen)
        {
            IsPackagingFormOpen = false;
        }
        else if (IsPurchaseFormOpen)
        {
            ClosePurchase();
        }
        else if (IsDetailsOpen)
        {
            IsDetailsOpen = false;
        }
        else if (IsIngredientFormOpen)
        {
            IsIngredientFormOpen = false;
        }
        else if (IsPackagingOpen)
        {
            IsPackagingOpen = false;
        }
    }

    [RelayCommand]
    private void NewIngredient()
    {
        _editingIngredientId = null;
        IngredientFormTitle = "Новый ингредиент";
        FormName = string.Empty;
        FormBrand = string.Empty;
        FormCategorySelection = NoCategory;
        FormNewCategory = string.Empty;
        FormUnit = Units.Grams;
        IsUnitLocked = false;
        IngredientFormError = null;
        PageError = null;
        IsIngredientFormOpen = true;
    }

    public void EditIngredient(IngredientRowViewModel row)
    {
        _editingIngredientId = row.Id;
        IngredientFormTitle = "Изменить ингредиент";
        FormName = row.Name;
        FormBrand = row.Brand ?? string.Empty;
        FormCategorySelection = row.CategoryName is { } category && CategoryOptions.Contains(category)
            ? category
            : NoCategory;
        FormNewCategory = string.Empty;
        FormUnit = row.Unit;
        IsUnitLocked = row.PurchaseCount > 0;
        IngredientFormError = null;
        PageError = null;
        IsIngredientFormOpen = true;
    }

    [RelayCommand]
    private void SaveIngredient()
    {
        try
        {
            var categoryName = ResolveCategoryName();

            if (_editingIngredientId is { } id)
            {
                _service.UpdateIngredient(id, FormName, FormBrand, FormUnit, categoryName);
            }
            else
            {
                _service.AddIngredient(FormName, FormBrand, FormUnit, categoryName);
            }

            IngredientFormError = null;
            IsIngredientFormOpen = false;
            ReloadOptions();
            Reload();
        }
        catch (ValidationException ex)
        {
            IngredientFormError = ex.Message;
        }
    }

    public void RequestDeleteIngredient(IngredientRowViewModel row)
    {
        _confirmAction = () =>
        {
            try
            {
                _service.MoveIngredientToTrash(row.Id);
                PageError = null;
                ReloadOptions();
                Reload();
            }
            catch (ValidationException ex)
            {
                PageError = ex.Message;
            }
        };

        ConfirmText = $"Переместить ингредиент «{row.Name}» в корзину? Его можно будет восстановить.";
        ConfirmButtonText = "В корзину";
        IsConfirmOpen = true;
    }

    [RelayCommand]
    private void NewPurchase()
    {
        if (!IngredientOptions.Any(option => !option.IsHeader))
        {
            PageError = "Сначала добавьте хотя бы один ингредиент.";
            return;
        }

        OpenPurchaseFor(IngredientOptions.First(option => !option.IsHeader).Id);
    }

    public void OpenPurchaseFor(IngredientRowViewModel row) => OpenPurchaseFor(row.Id);

    private void OpenPurchaseFor(Guid ingredientId)
    {
        _editingPurchaseId = null;
        IsPurchaseEditing = false;
        PurchaseFormTitle = "Новая закупка";

        PurchaseIngredient = IngredientOptions.FirstOrDefault(option => !option.IsHeader && option.Id == ingredientId)
            ?? IngredientOptions.FirstOrDefault(option => !option.IsHeader);

        if (PurchaseIngredient is null)
        {
            PageError = "Сначала добавьте хотя бы один ингредиент.";
            return;
        }

        PurchaseDate = DateTimeOffset.Now;
        PurchaseStore = string.Empty;
        PurchaseQuantity = null;
        PurchaseTotal = null;
        PurchaseNote = string.Empty;
        PurchaseFormError = null;
        PageError = null;
        UpdatePurchaseUnit();
        UpdatePurchasePreview();
        IsPurchaseFormOpen = true;
    }

    public void RequestEditPurchase(PurchaseRowViewModel row)
    {
        var purchase = row.Model;

        _editingPurchaseId = purchase.Id;
        IsPurchaseEditing = true;
        PurchaseFormTitle = "Изменить закупку";
        PurchaseIngredient = IngredientOptions.FirstOrDefault(option => !option.IsHeader && option.Id == purchase.IngredientId)
            ?? PurchaseIngredient;
        PurchaseDate = new DateTimeOffset(purchase.PurchasedOn.ToDateTime(TimeOnly.MinValue));
        PurchaseStore = purchase.Store ?? string.Empty;
        PurchaseQuantity = purchase.Quantity;
        PurchaseTotal = purchase.TotalPrice;
        PurchaseNote = purchase.Note ?? string.Empty;
        PurchaseFormError = null;
        PageError = null;
        UpdatePurchaseUnit();
        UpdatePurchasePreview();
        IsPurchaseFormOpen = true;
    }

    [RelayCommand]
    private void SavePurchase()
    {
        try
        {
            if (PurchaseIngredient is null)
            {
                PurchaseFormError = "Выберите ингредиент.";
                return;
            }

            if (PurchaseDate is null)
            {
                PurchaseFormError = "Укажите дату закупки.";
                return;
            }

            var date = DateOnly.FromDateTime(PurchaseDate.Value.Date);

            if (_editingPurchaseId is { } purchaseId)
            {
                _service.UpdatePurchase(
                    purchaseId,
                    date,
                    PurchaseStore,
                    PurchaseQuantity ?? 0m,
                    PurchaseTotal ?? 0m,
                    PurchaseNote);
            }
            else
            {
                _service.AddPurchase(
                    PurchaseIngredient.Id,
                    date,
                    PurchaseStore,
                    PurchaseQuantity ?? 0m,
                    PurchaseTotal ?? 0m,
                    note: PurchaseNote);
            }

            PurchaseFormError = null;
            ClosePurchase();
            Reload();
            RefreshDetails();
        }
        catch (ValidationException ex)
        {
            PurchaseFormError = ex.Message;
        }
    }

    public void OpenDetails(IngredientRowViewModel row)
    {
        _detailsIngredientId = row.Id;
        DetailsError = null;
        RefreshDetails();
        IsDetailsOpen = true;
    }

    public void RequestDeletePurchase(PurchaseRowViewModel row)
    {
        _confirmAction = () =>
        {
            try
            {
                _service.DeletePurchase(row.Id);
                DetailsError = null;
                Reload();
                RefreshDetails();
            }
            catch (ValidationException ex)
            {
                DetailsError = ex.Message;
            }
        };

        ConfirmText = $"Удалить закупку от {row.DateText}? Средняя цена пересчитается.";
        ConfirmButtonText = "Удалить закупку";
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

    [RelayCommand]
    private void CloseDetails() => IsDetailsOpen = false;

    [RelayCommand]
    private void AddPurchaseToDetails()
    {
        if (_detailsIngredientId is { } id)
        {
            OpenPurchaseFor(id);
        }
    }

    [RelayCommand]
    private void ClosePurchaseForm() => ClosePurchase();

    private void ClosePurchase()
    {
        IsPurchaseFormOpen = false;
        IsPurchaseEditing = false;
        _editingPurchaseId = null;
    }

    [RelayCommand]
    private void CloseIngredientForm() => IsIngredientFormOpen = false;

    // ======================= упаковка =======================

    [RelayCommand]
    private void OpenPackaging()
    {
        PackagingError = null;
        ReloadPackaging();
        IsPackagingOpen = true;
    }

    [RelayCommand]
    private void ClosePackaging()
    {
        IsPackagingFormOpen = false;
        IsPackagingOpen = false;
    }

    [RelayCommand]
    private void NewPackaging()
    {
        _editingPackagingId = null;
        PackagingFormTitle = "Новая упаковка";
        PackagingFormName = string.Empty;
        PackagingFormSize = string.Empty;
        PackagingFormPrice = 100m;
        PackagingFormCategorySelection = NoCategory;
        PackagingFormError = null;
        PackagingError = null;
        IsPackagingFormOpen = true;
    }

    public void EditPackaging(PackagingRowViewModel row)
    {
        _editingPackagingId = row.Id;
        PackagingFormTitle = "Изменить упаковку";
        PackagingFormName = row.Name;
        PackagingFormSize = row.Model.Item.Size ?? string.Empty;
        PackagingFormPrice = row.Model.Item.Price;
        PackagingFormCategorySelection = row.Model.CategoryName is { } category &&
                                         PackagingCategoryOptions.Contains(category)
            ? category
            : NoCategory;
        PackagingFormError = null;
        PackagingError = null;
        IsPackagingFormOpen = true;
    }

    [RelayCommand]
    private void SavePackaging()
    {
        try
        {
            var category = PackagingFormCategorySelection == NoCategory
                ? null
                : PackagingFormCategorySelection;

            if (_editingPackagingId is { } id)
            {
                _packaging.UpdateItem(id, PackagingFormName, PackagingFormSize, PackagingFormPrice ?? 0m, category);
            }
            else
            {
                _packaging.CreateItem(PackagingFormName, PackagingFormSize, PackagingFormPrice ?? 0m, category);
            }

            PackagingFormError = null;
            IsPackagingFormOpen = false;
            ReloadPackaging();
        }
        catch (ValidationException ex)
        {
            PackagingFormError = ex.Message;
        }
    }

    [RelayCommand]
    private void ClosePackagingForm() => IsPackagingFormOpen = false;

    [RelayCommand]
    private void AddPackagingCategory()
    {
        try
        {
            _packaging.CreateCategory(PackagingCategoryName);
            PackagingCategoryName = string.Empty;
            PackagingError = null;
            ReloadPackaging();
        }
        catch (ValidationException ex)
        {
            PackagingError = ex.Message;
        }
    }

    public void RequestDeletePackaging(PackagingRowViewModel row)
    {
        _confirmAction = () =>
        {
            try
            {
                _packaging.DeleteItem(row.Id);
                PackagingError = null;
                ReloadPackaging();
            }
            catch (ValidationException ex)
            {
                PackagingError = ex.Message;
            }
        };

        ConfirmText = $"Удалить упаковку «{row.Name}»? В существующих заказах она останется.";
        ConfirmButtonText = "Удалить упаковку";
        IsConfirmOpen = true;
    }

    public void RequestDeletePackagingCategory(PackagingGroupViewModel group)
    {
        _confirmAction = () =>
        {
            try
            {
                _packaging.DeleteCategory(group.Name);
                PackagingError = null;

                _expandedPackagingGroups.Remove(group.Name);
                _expandedPackagingGroups.Add(NoCategory);

                ReloadPackaging();
            }
            catch (ValidationException ex)
            {
                PackagingError = ex.Message;
            }
        };

        ConfirmText = $"Удалить категорию «{group.Name}»? Упаковки перейдут в «Без категории».";
        ConfirmButtonText = "Удалить категорию";
        IsConfirmOpen = true;
    }

    private void ReloadPackaging()
    {
        var items = _packaging.GetItems();
        var categories = _packaging.GetCategories();

        PackagingRows.Clear();
        foreach (var item in items)
        {
            PackagingRows.Add(new PackagingRowViewModel(item, this));
        }

        var byCategory = PackagingRows
            .GroupBy(row => row.CategoryText)
            .ToDictionary(group => group.Key, group => group.ToList());

        foreach (var category in categories)
        {
            byCategory.TryAdd(category.Name, new List<PackagingRowViewModel>());
        }

        PackagingGroups.Clear();
        var ordered = byCategory
            .OrderBy(group => group.Key == NoCategory ? 1 : 0)
            .ThenBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase);

        foreach (var group in ordered)
        {
            PackagingGroups.Add(new PackagingGroupViewModel(
                group.Key,
                group.Value,
                TogglePackagingGroup,
                RequestDeletePackagingCategory)
            {
                IsExpanded = _expandedPackagingGroups.Contains(group.Key),
            });
        }

        PackagingCategoryOptions.Clear();
        PackagingCategoryOptions.Add(NoCategory);
        foreach (var category in categories)
        {
            PackagingCategoryOptions.Add(category.Name);
        }

        PackagingSummary = items.Count == 0
            ? "Пока нет упаковок — добавьте первую"
            : $"{items.Count} {Formats.Plural(items.Count, "упаковка", "упаковки", "упаковок")} · " +
              $"{categories.Count} {Formats.Plural(categories.Count, "категория", "категории", "категорий")}";

        OnPropertyChanged(nameof(IsPackagingEmpty));
    }

    private void TogglePackagingGroup(PackagingGroupViewModel group)
    {
        if (group.IsExpanded)
        {
            _expandedPackagingGroups.Add(group.Name);
        }
        else
        {
            _expandedPackagingGroups.Remove(group.Name);
        }
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private string? ResolveCategoryName()
    {
        if (!string.IsNullOrWhiteSpace(FormNewCategory))
        {
            return FormNewCategory.Trim();
        }

        return FormCategorySelection == NoCategory ? null : FormCategorySelection;
    }

    partial void OnPurchaseIngredientChanged(IngredientPickItem? value)
    {
        UpdatePurchaseUnit();
        UpdatePurchasePreview();
    }

    partial void OnPurchaseQuantityChanged(decimal? value) => UpdatePurchasePreview();

    partial void OnPurchaseTotalChanged(decimal? value) => UpdatePurchasePreview();

    private void UpdatePurchaseUnit() => PurchaseUnit = PurchaseIngredient?.Unit ?? Units.Grams;

    private void UpdatePurchasePreview()
    {
        if (PurchaseQuantity is > 0m && PurchaseTotal is >= 0m && PurchaseIngredient is not null)
        {
            PurchasePreview = Formats.PerUnit(
                PurchaseTotal.Value / PurchaseQuantity.Value,
                PurchaseIngredient.Unit);
        }
        else
        {
            PurchasePreview = "—";
        }
    }

    private void RefreshDetails()
    {
        if (_detailsIngredientId is not { } id)
        {
            return;
        }

        try
        {
            var details = _service.GetDetails(id);
            DetailsTitle = details.Ingredient.Name;
            DetailsBrand = string.IsNullOrWhiteSpace(details.Ingredient.Brand) ? "Без бренда" : details.Ingredient.Brand!;
            DetailsCategory = details.CategoryName ?? string.Empty;
            DetailsHasCategory = details.CategoryName is not null;
            DetailsAverage = details.AveragePricePerUnit is { } average
                ? Formats.PerUnit(average, details.Ingredient.Unit)
                : "—";
            DetailsTotalSpent = Formats.Money0(details.TotalSpent);
            DetailsTotalQuantity = Formats.Quantity(details.TotalQuantity, details.Ingredient.Unit);

            DetailsPurchases.Clear();
            foreach (var purchase in details.Purchases)
            {
                DetailsPurchases.Add(new PurchaseRowViewModel(purchase, details.Ingredient.Unit, this));
            }

            OnPropertyChanged(nameof(DetailsEmpty));
        }
        catch (ValidationException)
        {
            IsDetailsOpen = false;
            _detailsIngredientId = null;
        }
    }

    private void Reload()
    {
        _summaries = _service.GetIngredients().ToList();

        IngredientOptions.Clear();
        var prices = _summaries.Select(summary => new IngredientPrice(
            summary.Ingredient.Id,
            summary.Ingredient.Name,
            summary.Ingredient.Unit,
            summary.CategoryName,
            summary.AveragePricePerUnit));

        foreach (var item in IngredientPickList.Build(prices))
        {
            IngredientOptions.Add(item);
        }

        ApplyFilter();
        UpdateOverview();

        OnPropertyChanged(nameof(HasIngredients));
    }

    private void ReloadOptions()
    {
        CategoryOptions.Clear();
        CategoryOptions.Add(NoCategory);

        foreach (var name in _service.GetCategoryNames())
        {
            CategoryOptions.Add(name);
        }
    }

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        var searchActive = query.Length > 0;

        var filtered = _summaries.Where(summary =>
        {
            if (query.Length == 0)
            {
                return true;
            }

            return summary.Ingredient.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                   (summary.Ingredient.Brand?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false) ||
                   (summary.CategoryName?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false);
        }).ToList();

        Groups.Clear();

        var filteredGroups = filtered
            .GroupBy(summary => summary.CategoryName ?? NoCategory)
            .ToDictionary(group => group.Key, group => group.ToList());

        if (!searchActive)
        {
            foreach (var name in _service.GetCategoryNames())
            {
                filteredGroups.TryAdd(name, new List<IngredientSummary>());
            }
        }

        var ordered = filteredGroups
            .OrderBy(group => group.Key == NoCategory ? 1 : 0)
            .ThenBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase);

        foreach (var group in ordered)
        {
            var rows = group.Value
                .OrderBy(summary => summary.Ingredient.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(summary => new IngredientRowViewModel(summary, this))
                .ToList();

            var viewModel = new IngredientGroupViewModel(
                group.Key,
                rows,
                ToggleGroup,
                RequestDeleteCategory,
                RequestRenameCategory)
            {
                IsExpanded = searchActive || _expandedGroups.Contains(group.Key),
            };

            Groups.Add(viewModel);
        }

        PageError = null;
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyText));
    }

    private void ToggleGroup(IngredientGroupViewModel group)
    {
        if (group.IsExpanded)
        {
            _expandedGroups.Add(group.Name);
        }
        else
        {
            _expandedGroups.Remove(group.Name);
        }
    }

    public void RequestRenameCategory(IngredientGroupViewModel group)
    {
        _renamingGroup = group;
        CategoryRenameValue = group.Name;
        CategoryRenameError = null;
        IsCategoryRenameOpen = true;
    }

    [RelayCommand]
    private void SaveCategoryRename()
    {
        if (_renamingGroup is null)
        {
            return;
        }

        try
        {
            var oldName = _renamingGroup.Name;
            var wasExpanded = _expandedGroups.Contains(oldName);
            _service.RenameCategory(oldName, CategoryRenameValue);
            if (wasExpanded)
            {
                _expandedGroups.Remove(oldName);
                _expandedGroups.Add(CategoryRenameValue.Trim());
            }

            CategoryRenameError = null;
            IsCategoryRenameOpen = false;
            _renamingGroup = null;
            PageError = null;
            ReloadOptions();
            Reload();
        }
        catch (ValidationException ex)
        {
            CategoryRenameError = ex.Message;
        }
    }

    [RelayCommand]
    private void CloseCategoryRename()
    {
        IsCategoryRenameOpen = false;
        _renamingGroup = null;
    }

    private void RequestDeleteCategory(IngredientGroupViewModel group)
    {
        _confirmAction = () =>
        {
            try
            {
                _service.DeleteCategory(group.Name);
                PageError = null;

                _expandedGroups.Remove(group.Name);
                _expandedGroups.Add(NoCategory);

                ReloadOptions();
                Reload();
            }
            catch (ValidationException ex)
            {
                PageError = ex.Message;
            }
        };

        ConfirmText = $"Удалить категорию «{group.Name}»? Ингредиенты перейдут в «Без категории».";
        ConfirmButtonText = "Удалить категорию";
        IsConfirmOpen = true;
    }

    private void UpdateOverview()
    {
        var overview = _service.GetOverview(DateOnly.FromDateTime(DateTime.Now));

        Subtitle = _summaries.Count == 0
            ? "Пока пусто — добавьте первый ингредиент"
            : $"{overview.IngredientCount} {Formats.Plural(overview.IngredientCount, "ингредиент", "ингредиента", "ингредиентов")} · {Formats.Purchases(overview.PurchaseCount)}";

        SpentThisMonthText = Formats.Money0(overview.SpentThisMonth);
        PurchasesThisMonthText = overview.PurchasesThisMonth.ToString();

        if (overview.LargestPurchaseThisMonth is { } largest)
        {
            LargestPurchaseValue = Formats.Money0(largest.TotalPrice);
            LargestPurchaseNote = $"{overview.LargestPurchaseIngredientName} · {Formats.RelativeDate(largest.PurchasedOn)}";
        }
        else
        {
            LargestPurchaseValue = "—";
            LargestPurchaseNote = "В этом месяце закупок не было";
        }
    }
}

/// <summary>Категория в справочнике: раскрывается и показывает свои ингредиенты.</summary>
public sealed partial class IngredientGroupViewModel : ViewModelBase
{
    private readonly Action<IngredientGroupViewModel> _onToggle;
    private readonly Action<IngredientGroupViewModel> _onDelete;
    private readonly Action<IngredientGroupViewModel> _onRename;

    public IngredientGroupViewModel(
        string name,
        IReadOnlyList<IngredientRowViewModel> items,
        Action<IngredientGroupViewModel> onToggle,
        Action<IngredientGroupViewModel> onDelete,
        Action<IngredientGroupViewModel> onRename)
    {
        Name = name;
        Items = items;
        CountText = Formats.Ingredients(items.Count);
        _onToggle = onToggle;
        _onDelete = onDelete;
        _onRename = onRename;
    }

    public string Name { get; }

    public IReadOnlyList<IngredientRowViewModel> Items { get; }

    public string CountText { get; }

    public bool CanDeleteCategory => !string.Equals(Name, "Без категории", StringComparison.Ordinal);

    public bool IsEmpty => Items.Count == 0;

    public bool ShowEmptyHint => IsExpanded && IsEmpty;

    [ObservableProperty]
    private bool _isExpanded;

    public string Chevron => IsExpanded ? "▾" : "▸";

    [RelayCommand]
    private void Toggle()
    {
        IsExpanded = !IsExpanded;
        _onToggle(this);
    }

    [RelayCommand]
    private void DeleteCategory() => _onDelete(this);

    [RelayCommand]
    private void RenameCategory() => _onRename(this);

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(Chevron));
        OnPropertyChanged(nameof(ShowEmptyHint));
    }
}

/// <summary>Строка справочника.</summary>
public sealed class IngredientRowViewModel : ViewModelBase
{
    private static readonly (string Background, string Foreground)[] AvatarPalette =
    {
        ("#F6EBD9", "#8B6A3E"),
        ("#F6E1E1", "#A5433A"),
        ("#E9EDE2", "#5C6B4C"),
        ("#EFE3DA", "#7A5136"),
        ("#E6EDF2", "#4A6E86"),
    };

    public IngredientRowViewModel(IngredientSummary summary, IngredientsViewModel owner)
    {
        Id = summary.Ingredient.Id;
        Name = summary.Ingredient.Name;
        Brand = summary.Ingredient.Brand;
        CategoryName = summary.CategoryName;
        Unit = summary.Ingredient.Unit;
        PurchaseCount = summary.PurchaseCount;
        Initials = BuildInitials(Name);

        var palette = AvatarPalette[StableHash(Name) % AvatarPalette.Length];
        AvatarBackground = new SolidColorBrush(Color.Parse(palette.Background));
        AvatarForeground = new SolidColorBrush(Color.Parse(palette.Foreground));

        AveragePriceText = summary.AveragePricePerUnit is { } average
            ? Formats.Price(average)
            : "—";
        AverageUnitText = "/ " + Unit;

        PurchaseCountText = summary.PurchaseCount == 0
            ? "Нет закупок"
            : $"{Formats.Purchases(summary.PurchaseCount)} · {Formats.Quantity(summary.TotalQuantity, Unit)}";

        if (summary.LastPurchase is { } last)
        {
            LastPurchaseText = $"{Formats.RelativeDate(last.PurchasedOn)} · {last.Store ?? "без магазина"}";
            LastPurchaseDetails = $"{Formats.Money(last.TotalPrice)} за {Formats.Quantity(last.Quantity, Unit)}";
        }
        else
        {
            LastPurchaseText = "Пока не покупали";
            LastPurchaseDetails = string.Empty;
        }

        PriceTooltip = BuildTooltip(summary);

        EditCommand = new RelayCommand(() => owner.EditIngredient(this));
        DeleteCommand = new RelayCommand(() => owner.RequestDeleteIngredient(this));
        AddPurchaseCommand = new RelayCommand(() => owner.OpenPurchaseFor(this));
        OpenDetailsCommand = new RelayCommand(() => owner.OpenDetails(this));
    }

    public Guid Id { get; }

    public string Name { get; }

    public string? Brand { get; }

    public bool HasBrand => !string.IsNullOrWhiteSpace(Brand);

    public string? CategoryName { get; }

    public bool HasCategory => !string.IsNullOrWhiteSpace(CategoryName);

    public string CategoryText => CategoryName ?? "Без категории";

    public string Unit { get; }

    public int PurchaseCount { get; }

    public bool CanDelete => PurchaseCount == 0;

    public string DeleteToolTip => CanDelete
        ? "Удалить ингредиент"
        : "Есть закупки — сначала удалите их в истории";

    public string Initials { get; }

    public IBrush AvatarBackground { get; }

    public IBrush AvatarForeground { get; }

    public string AveragePriceText { get; }

    public string AverageUnitText { get; }

    public string PurchaseCountText { get; }

    public string LastPurchaseText { get; }

    public string LastPurchaseDetails { get; }

    public string PriceTooltip { get; }

    public IRelayCommand EditCommand { get; }

    public IRelayCommand DeleteCommand { get; }

    public IRelayCommand AddPurchaseCommand { get; }

    public IRelayCommand OpenDetailsCommand { get; }

    private static string BuildInitials(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 2)
        {
            return string.Concat(char.ToUpperInvariant(words[0][0]), char.ToUpperInvariant(words[1][0]));
        }

        return name.Length >= 2
            ? name[..2].ToUpperInvariant()
            : name.ToUpperInvariant();
    }

    private static int StableHash(string value)
    {
        var hash = 0;
        foreach (var character in value)
        {
            hash = unchecked(hash * 31 + character) & 0x7FFFFFFF;
        }

        return hash;
    }

    private static string BuildTooltip(IngredientSummary summary)
    {
        if (summary.LastPurchase is not { } last)
        {
            return $"{summary.Ingredient.Name}\nЗакупок пока нет";
        }

        var average = summary.AveragePricePerUnit is { } value
            ? Formats.PerUnit(value, summary.Ingredient.Unit)
            : "—";

        var lines = new List<string>
        {
            $"{summary.Ingredient.Name} · средняя {average}",
            string.Empty,
            $"{Formats.Date(last.PurchasedOn)} · {last.Store ?? "без магазина"}",
            $"{Formats.Money(last.TotalPrice)} за {Formats.Quantity(last.Quantity, summary.Ingredient.Unit)}",
        };

        if (summary.PurchaseCount > 1)
        {
            lines.Add(string.Empty);
            lines.Add($"Ещё {Formats.Purchases(summary.PurchaseCount - 1)} — нажмите, чтобы открыть историю");
        }

        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>Строка истории закупок в панели ингредиента.</summary>
public sealed class PurchaseRowViewModel : ViewModelBase
{
    public PurchaseRowViewModel(Purchase purchase, string unit, IngredientsViewModel owner)
    {
        Model = purchase;
        Id = purchase.Id;
        DateText = Formats.Date(purchase.PurchasedOn);
        StoreText = string.IsNullOrWhiteSpace(purchase.Store) ? "Магазин не указан" : purchase.Store!;
        QuantityText = Formats.Quantity(purchase.Quantity, unit);
        TotalText = Formats.Money(purchase.TotalPrice);
        UnitPriceText = Formats.PerUnit(purchase.PricePerUnit, unit);
        NoteText = purchase.Note ?? string.Empty;
        HasNote = !string.IsNullOrWhiteSpace(purchase.Note);
        EditCommand = new RelayCommand(() => owner.RequestEditPurchase(this));
        DeleteCommand = new RelayCommand(() => owner.RequestDeletePurchase(this));
    }

    public Purchase Model { get; }

    public Guid Id { get; }

    public string DateText { get; }

    public string StoreText { get; }

    public string QuantityText { get; }

    public string TotalText { get; }

    public string UnitPriceText { get; }

    public string NoteText { get; }

    public bool HasNote { get; }

    public IRelayCommand EditCommand { get; }

    public IRelayCommand DeleteCommand { get; }
}

/// <summary>Строка списка упаковок.</summary>
public sealed class PackagingRowViewModel : ViewModelBase
{
    public PackagingRowViewModel(PackagingSummary summary, IngredientsViewModel owner)
    {
        Model = summary;
        Id = summary.Item.Id;
        Name = summary.Item.Name;
        CategoryText = summary.CategoryName ?? "Без категории";
        SizeText = string.IsNullOrWhiteSpace(summary.Item.Size) ? "—" : summary.Item.Size!;
        PriceText = Formats.Money(summary.Item.Price);
        EditCommand = new RelayCommand(() => owner.EditPackaging(this));
        DeleteCommand = new RelayCommand(() => owner.RequestDeletePackaging(this));
    }

    public PackagingSummary Model { get; }

    public Guid Id { get; }

    public string Name { get; }

    public string CategoryText { get; }

    public string SizeText { get; }

    public string PriceText { get; }

    public IRelayCommand EditCommand { get; }

    public IRelayCommand DeleteCommand { get; }
}

/// <summary>Категория упаковки: раскрывается и показывает свои коробки.</summary>
public sealed partial class PackagingGroupViewModel : ViewModelBase
{
    private readonly Action<PackagingGroupViewModel> _onToggle;
    private readonly Action<PackagingGroupViewModel> _onDelete;

    public PackagingGroupViewModel(
        string name,
        IReadOnlyList<PackagingRowViewModel> items,
        Action<PackagingGroupViewModel> onToggle,
        Action<PackagingGroupViewModel> onDelete)
    {
        Name = name;
        Items = items;
        CountText = items.Count + " " + Formats.Plural(items.Count, "упаковка", "упаковки", "упаковок");
        _onToggle = onToggle;
        _onDelete = onDelete;
    }

    public string Name { get; }

    public IReadOnlyList<PackagingRowViewModel> Items { get; }

    public string CountText { get; }

    public bool CanDeleteCategory => !string.Equals(Name, "Без категории", StringComparison.Ordinal);

    public bool IsEmpty => Items.Count == 0;

    public bool ShowEmptyHint => IsExpanded && IsEmpty;

    [ObservableProperty]
    private bool _isExpanded;

    public string Chevron => IsExpanded ? "▾" : "▸";

    [RelayCommand]
    private void Toggle()
    {
        IsExpanded = !IsExpanded;
        _onToggle(this);
    }

    [RelayCommand]
    private void DeleteCategory() => _onDelete(this);

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(Chevron));
        OnPropertyChanged(nameof(ShowEmptyHint));
    }
}
