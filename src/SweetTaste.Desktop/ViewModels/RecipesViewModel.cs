using System.Collections.ObjectModel;
using System.IO;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SweetTaste.Core.Domain;
using SweetTaste.Core.Services;

namespace SweetTaste.Desktop.ViewModels;

/// <summary>Книга рецептов: список, состав, себестоимость и цена.</summary>
public sealed partial class RecipesViewModel : ViewModelBase, IFormPage
{
    private const string NoCategory = "Без категории";

    private readonly RecipeService _service;
    private readonly SettingsService _settings;
    private readonly HashSet<string> _expandedGroups = new();
    private List<RecipeSummary> _summaries = new();
    private Guid? _editingRecipeId;
    private RecipeRowViewModel? _previewRow;
    private Action? _confirmAction;
    private string _lastOutputUnit = RecipeUnits.Pieces;
    private bool _suppressUnitConversion;
    private byte[]? _formPhotoBytes;
    private string? _renamingCategoryName;

    public RecipesViewModel(RecipeService service, SettingsService settings)
    {
        _service = service;
        _settings = settings;
        ReloadOptions();
        Reload();
    }

    public ObservableCollection<RecipeGroupViewModel> Groups { get; } = new();

    public ObservableCollection<string> CategoryOptions { get; } = new();

    public ObservableCollection<IngredientPickItem> IngredientOptions { get; } = new();

    public ObservableCollection<RecipeLineViewModel> Lines { get; } = new();

    public ObservableCollection<ScaledIngredientRowViewModel> ScaledLines { get; } = new();

    public ObservableCollection<RecipePreviewLineViewModel> ViewLines { get; } = new();

    public ObservableCollection<RecipePreviewStepViewModel> ViewSteps { get; } = new();

    public IReadOnlyList<string> OutputUnits => RecipeUnits.Allowed;

    /// <summary>Штучный выход: только целые числа; весовой — дробный.</summary>
    public bool IsPieceOutput => string.Equals(FormOutputUnit, RecipeUnits.Pieces, StringComparison.Ordinal);

    public decimal OutputMinimum => IsPieceOutput ? 1m : 0.001m;

    public decimal OutputIncrement => FormOutputUnit switch
    {
        RecipeUnits.Kilograms => 0.1m,
        RecipeUnits.Grams => 10m,
        _ => 1m,
    };

    public string OutputFormat => IsPieceOutput ? "0" : "0.###";

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _subtitle = string.Empty;

    [ObservableProperty]
    private string? _pageError;

    [ObservableProperty]
    private bool _isFormOpen;

    [ObservableProperty]
    private string _formTitle = "Новый рецепт";

    [ObservableProperty]
    private string _formName = string.Empty;

    [ObservableProperty]
    private string _formCategorySelection = NoCategory;

    [ObservableProperty]
    private string _formNewCategory = string.Empty;

    [ObservableProperty]
    private decimal? _formOutputQuantity = 1m;

    [ObservableProperty]
    private string _formOutputUnit = RecipeUnits.Pieces;

    [ObservableProperty]
    private decimal? _formMarkup = 100m;

    [ObservableProperty]
    private string _previewBatch = "—";

    [ObservableProperty]
    private string _previewUnit = "—";

    [ObservableProperty]
    private string _previewPrice = "—";

    [ObservableProperty]
    private string _formTechnology = string.Empty;

    [ObservableProperty]
    private Bitmap? _formPhoto;

    [ObservableProperty]
    private bool _formHasPhoto;

    [ObservableProperty]
    private decimal? _scaleQuantity;

    [ObservableProperty]
    private string _scaleTitle = "—";

    [ObservableProperty]
    private string? _formError;

    [ObservableProperty]
    private bool _isConfirmOpen;

    [ObservableProperty]
    private string _confirmText = string.Empty;

    [ObservableProperty]
    private string _confirmButtonText = "Удалить";

    [ObservableProperty]
    private bool _isCategoryRenameOpen;

    [ObservableProperty]
    private string _categoryRenameValue = string.Empty;

    [ObservableProperty]
    private string? _categoryRenameError;

    // --- просмотр рецепта ---
    [ObservableProperty]
    private bool _isPreviewOpen;

    [ObservableProperty]
    private string _viewTitle = string.Empty;

    [ObservableProperty]
    private string _viewCategory = string.Empty;

    [ObservableProperty]
    private bool _viewHasCategory;

    [ObservableProperty]
    private Bitmap? _viewPhoto;

    [ObservableProperty]
    private bool _viewPhotoVisible;

    [ObservableProperty]
    private string _viewOutput = "—";

    [ObservableProperty]
    private string _viewMarkup = "—";

    [ObservableProperty]
    private string _viewBatchCost = "—";

    [ObservableProperty]
    private string _viewPrice = "—";

    [ObservableProperty]
    private string _viewIngredientCount = string.Empty;

    [ObservableProperty]
    private string _viewStepCount = string.Empty;

    public bool ViewHasLines => ViewLines.Count > 0;

    public bool ViewHasTechnology => ViewSteps.Count > 0;

    public bool IsEmpty => Groups.Count == 0;

    public string EmptyText => _summaries.Count == 0
        ? "Пока нет рецептов — добавьте первый."
        : "Ничего не найдено. Попробуйте другой запрос.";

    public bool CanCreateRecipe => IngredientOptions.Any(option => !option.IsHeader);

    public string CreateTooltip => CanCreateRecipe
        ? "Добавить рецепт"
        : "Сначала добавьте ингредиенты в справочник";

    bool IFormPage.IsFormOpen => IsFormOpen || IsConfirmOpen || IsPreviewOpen || IsCategoryRenameOpen;

    public void CancelForm()
    {
        if (IsConfirmOpen)
        {
            IsConfirmOpen = false;
            _confirmAction = null;
        }
        else if (IsPreviewOpen)
        {
            IsPreviewOpen = false;
            _previewRow = null;
        }
        else if (IsCategoryRenameOpen)
        {
            IsCategoryRenameOpen = false;
            _renamingCategoryName = null;
        }
        else if (IsFormOpen)
        {
            CloseForm();
        }
    }

    [RelayCommand]
    private void NewRecipe()
    {
        if (!CanCreateRecipe)
        {
            PageError = "Сначала добавьте ингредиенты в справочник.";
            return;
        }

        ReloadOptions();
        _editingRecipeId = null;
        FormTitle = "Новый рецепт";
        FormName = string.Empty;
        FormCategorySelection = NoCategory;
        FormNewCategory = string.Empty;
        SetOutput(1m, _settings.DefaultRecipeUnit);
        FormMarkup = _settings.DefaultMarkupPercent;
        FormTechnology = string.Empty;
        _formPhotoBytes = null;
        FormPhoto = null;
        FormHasPhoto = false;
        ScaleQuantity = FormOutputQuantity;
        FormError = null;
        PageError = null;
        Lines.Clear();
        AddLine();
        UpdatePreview();
        IsFormOpen = true;
    }

    public void EditRecipe(RecipeRowViewModel row)
    {
        var details = _service.GetRecipeDetails(row.Id);

        ReloadOptions();

        if (details.Lines.Any(line => IngredientOptions.All(option => option.IsHeader || option.Id != line.Ingredient.Id)))
        {
            PageError = "В рецепте есть ингредиент из корзины. Восстановите его, чтобы редактировать рецепт.";
            return;
        }

        _editingRecipeId = row.Id;
        FormTitle = "Изменить рецепт";
        FormName = details.Recipe.Name;
        FormCategorySelection = details.CategoryName is { } category && CategoryOptions.Contains(category)
            ? category
            : NoCategory;
        FormNewCategory = string.Empty;
        SetOutput(details.Recipe.OutputQuantity, details.Recipe.OutputUnit);
        FormMarkup = details.Recipe.MarkupPercent;
        FormTechnology = details.Recipe.Technology ?? string.Empty;
        _formPhotoBytes = details.Recipe.Photo;
        FormPhoto = TryLoadBitmap(_formPhotoBytes);
        FormHasPhoto = FormPhoto is not null;
        ScaleQuantity = details.Recipe.OutputQuantity;
        FormError = null;
        PageError = null;

        Lines.Clear();
        foreach (var line in details.Lines)
        {
            var option = IngredientOptions.FirstOrDefault(ingredient =>
                !ingredient.IsHeader && ingredient.Id == line.Ingredient.Id);
            if (option is not null)
            {
                Lines.Add(new RecipeLineViewModel(
                    IngredientOptions,
                    line.Amount,
                    option,
                    RemoveLine,
                    UpdatePreview));
            }
        }

        if (Lines.Count == 0)
        {
            AddLine();
        }

        UpdatePreview();
        IsFormOpen = true;
    }

    [RelayCommand]
    private void AddLine()
    {
        Lines.Add(new RecipeLineViewModel(
            IngredientOptions,
            amount: 100m,
            ingredient: IngredientOptions.FirstOrDefault(option => !option.IsHeader),
            RemoveLine,
            UpdatePreview));
        UpdatePreview();
    }

    private void RemoveLine(RecipeLineViewModel line)
    {
        Lines.Remove(line);
        UpdatePreview();
    }

    [RelayCommand]
    private void SaveRecipe()
    {
        try
        {
            var lines = Lines
                .Where(line => line.Ingredient is { IsHeader: false })
                .Select(line => (IngredientId: line.Ingredient!.Id, Amount: line.Amount.GetValueOrDefault()))
                .ToList();

            var categoryName = ResolveCategoryName();
            var quantity = decimal.Round(FormOutputQuantity ?? 0m, 3, MidpointRounding.AwayFromZero);
            var markup = (int)Math.Round(FormMarkup ?? 0);

            SweetTaste.Core.Domain.Recipe saved;

            if (_editingRecipeId is { } id)
            {
                saved = _service.UpdateRecipe(id, FormName, categoryName, quantity, FormOutputUnit, markup, lines,
                    FormTechnology);
            }
            else
            {
                saved = _service.CreateRecipe(FormName, categoryName, quantity, FormOutputUnit, markup, lines,
                    FormTechnology);
            }

            _service.SetRecipePhoto(saved.Id, _formPhotoBytes);

            FormError = null;
            IsFormOpen = false;
            _editingRecipeId = null;
            _formPhotoBytes = null;
            ReloadOptions();
            Reload();
        }
        catch (ValidationException ex)
        {
            FormError = ex.Message;
        }
    }

    [RelayCommand]
    private void CloseForm()
    {
        IsFormOpen = false;
        _editingRecipeId = null;
        FormError = null;
    }

    public void RequestDeleteRecipe(RecipeRowViewModel row)
    {
        _confirmAction = () =>
        {
            try
            {
                _service.MoveRecipeToTrash(row.Id);
                PageError = null;
                ReloadOptions();
                Reload();
            }
            catch (ValidationException ex)
            {
                PageError = ex.Message;
            }
        };

        ConfirmText = $"Переместить рецепт «{row.Name}» в корзину? Его можно будет восстановить.";
        ConfirmButtonText = "В корзину";
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

    public void OpenPreview(RecipeRowViewModel row)
    {
        try
        {
            var details = _service.GetRecipeDetails(row.Id);

            _previewRow = row;
            ViewTitle = details.Recipe.Name;
            ViewCategory = details.CategoryName ?? NoCategory;
            ViewHasCategory = details.CategoryName is not null;
            ViewPhoto = TryLoadBitmap(details.Recipe.Photo);
            ViewPhotoVisible = ViewPhoto is not null;
            ViewOutput = Formats.Output(details.Recipe.OutputQuantity, details.Recipe.OutputUnit);
            ViewMarkup = details.Recipe.MarkupPercent + "%";
            ViewBatchCost = Formats.Money(details.BatchCost);
            ViewPrice = Formats.UnitPrice(details.PricePerUnit, details.Recipe.OutputUnit);

            ViewLines.Clear();
            foreach (var line in details.Lines)
            {
                ViewLines.Add(new RecipePreviewLineViewModel(line));
            }

            ViewIngredientCount = Formats.Ingredients(details.Lines.Count);

            ViewSteps.Clear();
            var number = 1;
            foreach (var step in SplitTechnology(details.Recipe.Technology))
            {
                ViewSteps.Add(new RecipePreviewStepViewModel(number++, step));
            }

            ViewStepCount = ViewSteps.Count == 0
                ? "Технология не заполнена"
                : $"{ViewSteps.Count} {Formats.Plural(ViewSteps.Count, "шаг", "шага", "шагов")}";

            OnPropertyChanged(nameof(ViewHasLines));
            OnPropertyChanged(nameof(ViewHasTechnology));

            PageError = null;
            IsPreviewOpen = true;
        }
        catch (ValidationException ex)
        {
            PageError = ex.Message;
        }
    }

    [RelayCommand]
    private void ClosePreview()
    {
        IsPreviewOpen = false;
        _previewRow = null;
    }

    [RelayCommand]
    private void EditPreview()
    {
        if (_previewRow is not { } row)
        {
            return;
        }

        IsPreviewOpen = false;
        _previewRow = null;
        EditRecipe(row);
    }

    private static IEnumerable<string> SplitTechnology(string? technology)
    {
        if (string.IsNullOrWhiteSpace(technology))
        {
            yield break;
        }

        var normalized = technology.Replace("\r\n", "\n").Replace('\r', '\n');

        foreach (var raw in normalized.Split('\n'))
        {
            var line = StripStepPrefix(raw.Trim());
            if (line.Length > 0)
            {
                yield return line;
            }
        }
    }

    private static string StripStepPrefix(string line)
    {
        var digits = 0;
        while (digits < line.Length && char.IsDigit(line[digits]))
        {
            digits++;
        }

        if (digits > 0 && digits < line.Length && (line[digits] == '.' || line[digits] == ')'))
        {
            return line[(digits + 1)..].TrimStart();
        }

        if (line.Length > 1 && (line[0] == '-' || line[0] == '•' || line[0] == '—') &&
            char.IsWhiteSpace(line[1]))
        {
            return line[2..].TrimStart();
        }

        return line;
    }

    public void RequestRenameCategory(RecipeGroupViewModel group) => RequestRenameCategory(group.Name);

    private void RequestRenameCategory(string name)
    {
        _renamingCategoryName = name;
        CategoryRenameValue = name;
        CategoryRenameError = null;
        IsCategoryRenameOpen = true;
    }

    [RelayCommand]
    private void SaveCategoryRename()
    {
        if (_renamingCategoryName is null)
        {
            return;
        }

        try
        {
            var renamed = CategoryRenameValue.Trim();
            var wasExpanded = _expandedGroups.Contains(_renamingCategoryName);
            _service.RenameCategory(_renamingCategoryName, CategoryRenameValue);
            if (wasExpanded)
            {
                _expandedGroups.Remove(_renamingCategoryName);
                _expandedGroups.Add(renamed);
            }

            CategoryRenameError = null;
            IsCategoryRenameOpen = false;
            _renamingCategoryName = null;
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
        _renamingCategoryName = null;
    }

    public void RequestDeleteCategory(RecipeGroupViewModel group) => RequestDeleteCategory(group.Name);

    private void RequestDeleteCategory(string name)
    {
        _confirmAction = () =>
        {
            try
            {
                _service.DeleteCategory(name);
                PageError = null;

                _expandedGroups.Remove(name);
                _expandedGroups.Add(NoCategory);

                ReloadOptions();
                Reload();
            }
            catch (ValidationException ex)
            {
                PageError = ex.Message;
            }
        };

        ConfirmText = $"Удалить категорию «{name}»? Рецепты перейдут в «Без категории».";
        ConfirmButtonText = "Удалить категорию";
        IsConfirmOpen = true;
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnFormOutputQuantityChanged(decimal? value) => UpdatePreview();

    partial void OnFormMarkupChanged(decimal? value) => UpdatePreview();

    partial void OnScaleQuantityChanged(decimal? value) => UpdateScaledLines();

    public void SetPhoto(byte[] raw)
    {
        if (raw.Length > 20_000_000)
        {
            FormError = "Файл слишком большой — выберите изображение до 20 МБ.";
            return;
        }

        try
        {
            using var input = new MemoryStream(raw);
            var source = new Bitmap(input);
            var width = source.PixelSize.Width;
            var height = source.PixelSize.Height;
            var scale = Math.Min(1.0, 512.0 / Math.Max(width, height));

            var preview = scale < 1.0
                ? source.CreateScaledBitmap(
                    new PixelSize(
                        Math.Max(1, (int)Math.Round(width * scale)),
                        Math.Max(1, (int)Math.Round(height * scale))),
                    BitmapInterpolationMode.HighQuality)
                : source;

            using var output = new MemoryStream();
            preview.Save(output);

            _formPhotoBytes = output.ToArray();
            FormPhoto = preview;
            FormHasPhoto = true;
            FormError = null;
        }
        catch (Exception)
        {
            FormError = "Не удалось прочитать изображение. Подойдёт PNG, JPG или WebP.";
        }
    }

    [RelayCommand]
    private void RemovePhoto()
    {
        _formPhotoBytes = null;
        FormPhoto = null;
        FormHasPhoto = false;
    }

    partial void OnFormOutputUnitChanged(string value)
    {
        OnPropertyChanged(nameof(IsPieceOutput));
        OnPropertyChanged(nameof(OutputMinimum));
        OnPropertyChanged(nameof(OutputIncrement));
        OnPropertyChanged(nameof(OutputFormat));

        if (_suppressUnitConversion)
        {
            _lastOutputUnit = value;
            UpdatePreview();
            return;
        }

        if (FormOutputQuantity is { } quantity)
        {
            var converted = quantity;
            if (_lastOutputUnit == RecipeUnits.Kilograms && value == RecipeUnits.Grams)
            {
                converted = quantity * 1000m;
            }
            else if (_lastOutputUnit == RecipeUnits.Grams && value == RecipeUnits.Kilograms)
            {
                converted = quantity / 1000m;
            }

            if (string.Equals(value, RecipeUnits.Pieces, StringComparison.Ordinal))
            {
                converted = Math.Max(1m, Math.Ceiling(converted));
            }

            FormOutputQuantity = decimal.Round(converted, 3, MidpointRounding.AwayFromZero);
        }

        _lastOutputUnit = value;
        UpdatePreview();
    }

    private void SetOutput(decimal quantity, string unit)
    {
        _suppressUnitConversion = true;
        FormOutputQuantity = quantity;
        FormOutputUnit = unit;
        _suppressUnitConversion = false;
        _lastOutputUnit = unit;
    }

    private string? ResolveCategoryName()
    {
        if (!string.IsNullOrWhiteSpace(FormNewCategory))
        {
            return FormNewCategory.Trim();
        }

        return FormCategorySelection == NoCategory ? null : FormCategorySelection;
    }

    private void UpdatePreview()
    {
        UpdateScaledLines();

        var lines = Lines
            .Where(line => line.Ingredient is { IsHeader: false } && line.Amount.GetValueOrDefault() > 0m)
            .Select(line => (IngredientId: line.Ingredient!.Id, Amount: line.Amount!.Value))
            .ToList();

        if (lines.Count == 0)
        {
            PreviewBatch = "—";
            PreviewUnit = "—";
            PreviewPrice = "—";
            return;
        }

        var cost = _service.CalculateCost(lines);
        var outputQuantity = FormOutputQuantity.GetValueOrDefault();
        if (outputQuantity <= 0m)
        {
            PreviewBatch = Formats.Money(cost);
            PreviewUnit = "—";
            PreviewPrice = "—";
            return;
        }

        var markup = (int)Math.Round(FormMarkup ?? 0);
        var costPerUnit = _service.CalculateCostPerUnit(cost, outputQuantity);
        var price = _service.CalculatePricePerUnit(costPerUnit, markup, FormOutputUnit);

        PreviewBatch = Formats.Money(cost);
        PreviewUnit = Formats.UnitPrice(costPerUnit, FormOutputUnit);
        PreviewPrice = Formats.UnitPrice(price, FormOutputUnit);
    }

    private void UpdateScaledLines()
    {
        ScaledLines.Clear();

        var output = FormOutputQuantity.GetValueOrDefault();
        var target = ScaleQuantity.GetValueOrDefault();

        if (output <= 0m || target <= 0m)
        {
            ScaleTitle = "—";
            return;
        }

        var factor = target / output;
        ScaleTitle = "Состав на " + Formats.Quantity(target, FormOutputUnit);

        foreach (var line in Lines)
        {
            if (line.Ingredient is not { IsHeader: false } ingredient || line.Amount.GetValueOrDefault() <= 0m)
            {
                continue;
            }

            ScaledLines.Add(new ScaledIngredientRowViewModel(
                ingredient.Name,
                Formats.Quantity(line.Amount!.Value * factor, ingredient.Unit)));
        }
    }

    internal static Bitmap? TryLoadBitmap(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(bytes);
            return new Bitmap(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void ReloadOptions()
    {
        IngredientOptions.Clear();
        foreach (var item in IngredientPickList.Build(_service.GetIngredientPrices()))
        {
            IngredientOptions.Add(item);
        }

        CategoryOptions.Clear();
        CategoryOptions.Add(NoCategory);
        foreach (var category in _service.GetCategoryNames())
        {
            CategoryOptions.Add(category);
        }

        OnPropertyChanged(nameof(CanCreateRecipe));
        OnPropertyChanged(nameof(CreateTooltip));
    }

    private void Reload()
    {
        _summaries = _service.GetRecipes().ToList();
        ApplyFilter();
        UpdateSubtitle();
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

            return summary.Recipe.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
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
                filteredGroups.TryAdd(name, new List<RecipeSummary>());
            }
        }

        var ordered = filteredGroups
            .OrderBy(group => group.Key == NoCategory ? 1 : 0)
            .ThenBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase);

        foreach (var group in ordered)
        {
            var rows = group.Value
                .OrderBy(summary => summary.Recipe.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(summary => new RecipeRowViewModel(summary, this))
                .ToList();

            var viewModel = new RecipeGroupViewModel(
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

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyText));
    }

    private void ToggleGroup(RecipeGroupViewModel group)
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

    private void UpdateSubtitle()
    {
        var recipeCount = _summaries.Count;
        var categoryCount = _service.GetCategoryNames().Count;

        if (recipeCount == 0)
        {
            Subtitle = "Пока пусто — добавьте первый рецепт";
            return;
        }

        Subtitle = $"{recipeCount} {Formats.Plural(recipeCount, "рецепт", "рецепта", "рецептов")} · " +
                   $"{categoryCount} {Formats.Plural(categoryCount, "категория", "категории", "категорий")}";
    }
}

/// <summary>Категория в книге рецептов: раскрывается и показывает свои рецепты.</summary>
public sealed partial class RecipeGroupViewModel : ViewModelBase
{
    private readonly Action<RecipeGroupViewModel> _onToggle;
    private readonly Action<RecipeGroupViewModel> _onDelete;
    private readonly Action<RecipeGroupViewModel> _onRename;

    public RecipeGroupViewModel(
        string name,
        IReadOnlyList<RecipeRowViewModel> items,
        Action<RecipeGroupViewModel> onToggle,
        Action<RecipeGroupViewModel> onDelete,
        Action<RecipeGroupViewModel> onRename)
    {
        Name = name;
        Items = items;
        CountText = items.Count + " " + Formats.Plural(items.Count, "рецепт", "рецепта", "рецептов");
        _onToggle = onToggle;
        _onDelete = onDelete;
        _onRename = onRename;
    }

    public string Name { get; }

    public IReadOnlyList<RecipeRowViewModel> Items { get; }

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

/// <summary>Строка пересчёта рецепта на другое количество.</summary>
public sealed class ScaledIngredientRowViewModel : ViewModelBase
{
    public ScaledIngredientRowViewModel(string name, string amountText)
    {
        Name = name;
        AmountText = amountText;
    }

    public string Name { get; }

    public string AmountText { get; }
}

/// <summary>Строка состава в окне просмотра рецепта.</summary>
public sealed class RecipePreviewLineViewModel : ViewModelBase
{
    public RecipePreviewLineViewModel(RecipeLineDetails line)
    {
        Name = line.Ingredient.Name;
        AmountText = Formats.Quantity(line.Amount, line.Ingredient.Unit);
        CostText = line.AveragePricePerUnit is null ? "цена не задана" : Formats.Money(line.Cost);
    }

    public string Name { get; }

    public string AmountText { get; }

    public string CostText { get; }
}

/// <summary>Шаг технологии в окне просмотра рецепта.</summary>
public sealed class RecipePreviewStepViewModel : ViewModelBase
{
    public RecipePreviewStepViewModel(int number, string text)
    {
        Number = number.ToString();
        Text = text;
    }

    public string Number { get; }

    public string Text { get; }
}

/// <summary>Строка книги рецептов.</summary>
public sealed class RecipeRowViewModel : ViewModelBase
{
    public RecipeRowViewModel(RecipeSummary summary, RecipesViewModel owner)
    {
        Id = summary.Recipe.Id;
        Name = summary.Recipe.Name;
        CategoryText = summary.CategoryName ?? "Без категории";
        OutputText = Formats.Output(summary.Recipe.OutputQuantity, summary.Recipe.OutputUnit);
        IngredientsText = Formats.Ingredients(summary.IngredientCount);
        CostText = Formats.Money(summary.CostPerUnit);
        PriceText = Formats.MoneySmart(summary.PricePerUnit);
        PriceUnitText = "/ " + summary.Recipe.OutputUnit;
        Photo = RecipesViewModel.TryLoadBitmap(summary.Recipe.Photo);
        HasPhoto = Photo is not null;

        EditCommand = new RelayCommand(() => owner.EditRecipe(this));
        DeleteCommand = new RelayCommand(() => owner.RequestDeleteRecipe(this));
    }

    public Guid Id { get; }

    public string Name { get; }

    public string CategoryText { get; }

    public string OutputText { get; }

    public string IngredientsText { get; }

    public string CostText { get; }

    public string PriceText { get; }

    public string PriceUnitText { get; }

    public Bitmap? Photo { get; }

    public bool HasPhoto { get; }

    public IRelayCommand EditCommand { get; }

    public IRelayCommand DeleteCommand { get; }
}

/// <summary>Строка состава в форме рецепта.</summary>
public sealed partial class RecipeLineViewModel : ViewModelBase
{
    private readonly Action<RecipeLineViewModel> _remove;
    private readonly Action _changed;

    public RecipeLineViewModel(
        IEnumerable<IngredientPickItem> options,
        decimal amount,
        IngredientPickItem? ingredient,
        Action<RecipeLineViewModel> remove,
        Action changed)
    {
        Options = options.ToList();
        _amount = amount;
        _ingredient = ingredient ?? Options.FirstOrDefault(option => !option.IsHeader);
        _remove = remove;
        _changed = changed;
    }

    public IReadOnlyList<IngredientPickItem> Options { get; }

    [ObservableProperty]
    private IngredientPickItem? _ingredient;

    [ObservableProperty]
    private decimal? _amount = 100m;

    public string UnitText => Ingredient?.Unit ?? "г";

    [RelayCommand]
    private void Remove() => _remove(this);

    partial void OnIngredientChanged(IngredientPickItem? value)
    {
        OnPropertyChanged(nameof(UnitText));
        _changed();
    }

    partial void OnAmountChanged(decimal? value) => _changed();
}
