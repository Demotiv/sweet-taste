using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SweetTaste.Core.Domain;
using SweetTaste.Core.Services;

namespace SweetTaste.Desktop.ViewModels;

/// <summary>Заказы: текущие, архив, создание, статусы и список покупок.</summary>
public sealed partial class OrdersViewModel : ViewModelBase, IFormPage
{
    private readonly OrderService _orders;
    private readonly RecipeService _recipes;
    private readonly PackagingService _packaging;
    private readonly CustomerService _customers;
    private List<Order> _allOrders = new();
    private Guid? _editingOrderId;
    private Guid? _detailsOrderId;
    private Action? _confirmAction;
    private bool _suppressCustomerSearch;
    private bool _paidManuallyEdited;
    private bool _updatingPaidAutomatically;

    /// <summary>Сообщает главному окну, что список заказов изменился (напоминание о выдачах).</summary>
    public event Action? OrdersChanged;

    public OrdersViewModel(
        OrderService orders,
        RecipeService recipes,
        PackagingService packaging,
        CustomerService customers)
    {
        _orders = orders;
        _recipes = recipes;
        _packaging = packaging;
        _customers = customers;
        ReloadOptions();
        Reload();
    }

    public ObservableCollection<OrderRowViewModel> Items { get; } = new();

    public ObservableCollection<OrderRecipeOption> RecipeOptions { get; } = new();

    public ObservableCollection<OrderLineViewModel> Lines { get; } = new();

    public ObservableCollection<OrderItemDetailRowViewModel> DetailsItems { get; } = new();

    public ObservableCollection<OrderShoppingGroupViewModel> DetailsShoppingGroups { get; } = new();

    public ObservableCollection<PackagingOption> PackagingOptions { get; } = new();

    public ObservableCollection<OrderPackagingRowViewModel> DetailsPackaging { get; } = new();

    public ObservableCollection<CustomerSuggestionViewModel> CustomerSuggestions { get; } = new();

    public ObservableCollection<CustomerListRowViewModel> CustomerRows { get; } = new();

    public ObservableCollection<CustomerOrderRowViewModel> CustomerOrders { get; } = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _showArchive;

    [ObservableProperty]
    private bool _showCustomers;

    [ObservableProperty]
    private bool _isCustomerOpen;

    [ObservableProperty]
    private string _customerTitle = string.Empty;

    [ObservableProperty]
    private string _customerPhone = string.Empty;

    [ObservableProperty]
    private string _customerStats = string.Empty;

    [ObservableProperty]
    private string? _customerError;

    [ObservableProperty]
    private bool _canRepeatCustomerOrder;

    private Guid? _customerDetailsId;

    [ObservableProperty]
    private string _subtitle = string.Empty;

    [ObservableProperty]
    private string? _pageError;

    // --- форма заказа ---
    [ObservableProperty]
    private bool _isFormOpen;

    [ObservableProperty]
    private string _formTitle = "Новый заказ";

    [ObservableProperty]
    private string _formFirstName = string.Empty;

    [ObservableProperty]
    private string _formLastName = string.Empty;

    [ObservableProperty]
    private string _formPhone = string.Empty;

    [ObservableProperty]
    private DateTimeOffset? _formDueDate = DateTimeOffset.Now.AddDays(1);

    [ObservableProperty]
    private string _formComment = string.Empty;

    [ObservableProperty]
    private string _formTotalText = "0 ₽";

    [ObservableProperty]
    private decimal? _formPaidAmount = 0m;

    [ObservableProperty]
    private decimal? _formDiscountAmount = 0m;

    [ObservableProperty]
    private string _formRemainingText = "0 ₽";

    [ObservableProperty]
    private string _formBaseTotalText = "0 ₽";

    [ObservableProperty]
    private bool _formHasDiscount;

    [ObservableProperty]
    private string? _formError;

    // --- детали ---
    [ObservableProperty]
    private bool _isDetailsOpen;

    [ObservableProperty]
    private string _detailsNumber = string.Empty;

    [ObservableProperty]
    private string _detailsClient = string.Empty;

    [ObservableProperty]
    private string _detailsPhone = string.Empty;

    [ObservableProperty]
    private string _detailsCustomer = string.Empty;

    [ObservableProperty]
    private string _detailsCreated = string.Empty;

    [ObservableProperty]
    private string _detailsDue = string.Empty;

    [ObservableProperty]
    private string _detailsStatus = string.Empty;

    [ObservableProperty]
    private IBrush _detailsStatusBrush = Brushes.Gray;

    [ObservableProperty]
    private bool _detailsIsNew;

    [ObservableProperty]
    private bool _detailsIsCooking;

    [ObservableProperty]
    private bool _detailsIsReady;

    [ObservableProperty]
    private bool _detailsIsDone;

    [ObservableProperty]
    private bool _detailsIsCancelled;

    [ObservableProperty]
    private string _detailsComment = string.Empty;

    [ObservableProperty]
    private bool _detailsHasComment;

    [ObservableProperty]
    private string _detailsTotal = "0 ₽";

    [ObservableProperty]
    private string _detailsShoppingCost = "0 ₽";

    [ObservableProperty]
    private string _detailsPaidText = "0 ₽";

    [ObservableProperty]
    private string _detailsDiscountText = "0 ₽";

    [ObservableProperty]
    private bool _detailsHasDiscount;

    [ObservableProperty]
    private string _detailsRemainingText = "0 ₽";

    [ObservableProperty]
    private bool _detailsIsPaid;

    [ObservableProperty]
    private bool _canMarkPaid;

    [ObservableProperty]
    private string _detailsPackagingTotal = "0 ₽";

    [ObservableProperty]
    private bool _canEditPackaging;

    [ObservableProperty]
    private PackagingOption? _packagingToAdd;

    [ObservableProperty]
    private decimal? _packagingQuantity = 1;

    [ObservableProperty]
    private string _detailsPackagingNote = string.Empty;

    [ObservableProperty]
    private bool _hasPackagingNote;

    [ObservableProperty]
    private string _detailsMarkupText = string.Empty;

    [ObservableProperty]
    private bool _hasDetailsMarkup;

    [ObservableProperty]
    private string? _detailsError;

    [ObservableProperty]
    private string? _shoppingNotice;

    [ObservableProperty]
    private bool _hasShoppingNotice;

    // --- подтверждение ---
    [ObservableProperty]
    private bool _isConfirmOpen;

    [ObservableProperty]
    private string _confirmText = string.Empty;

    [ObservableProperty]
    private string _confirmButtonText = "Отменить заказ";

    public bool CurrentTabActive => !ShowArchive && !ShowCustomers;

    public bool ArchiveTabActive => ShowArchive;

    public bool CustomersTabActive => ShowCustomers;

    public bool IsEmpty => Items.Count == 0;

    public bool IsCustomerListEmpty => CustomerRows.Count == 0;

    public string EmptyText => SearchText.Trim().Length > 0
        ? "Ничего не найдено. Попробуйте другой запрос."
        : ShowArchive
            ? "В архиве пока пусто."
            : "Текущих заказов нет.";

    public string CustomerEmptyText => SearchText.Trim().Length > 0
        ? "Ничего не найдено. Попробуйте другой запрос."
        : "Клиентов пока нет — они появятся после первого заказа.";

    public bool CanCreateOrder => RecipeOptions.Count > 0;

    public string CreateTooltip => CanCreateOrder
        ? "Создать заказ"
        : "Сначала добавьте рецепты в книгу";

    public bool DetailsShoppingEmpty => DetailsShoppingGroups.Count == 0;

    public bool DetailsPackagingEmpty => DetailsPackaging.Count == 0;

    public bool HasCustomerSuggestions => CustomerSuggestions.Count > 0;

    public bool CustomerOrdersEmpty => CustomerOrders.Count == 0;

    public bool IsFormOpenForPage => IsFormOpen || IsDetailsOpen || IsConfirmOpen || IsCustomerOpen;

    bool IFormPage.IsFormOpen => IsFormOpen || IsDetailsOpen || IsConfirmOpen || IsCustomerOpen;

    public void CancelForm()
    {
        if (IsConfirmOpen)
        {
            IsConfirmOpen = false;
            _confirmAction = null;
        }
        else if (IsDetailsOpen)
        {
            IsDetailsOpen = false;
        }
        else if (IsCustomerOpen)
        {
            IsCustomerOpen = false;
        }
        else if (IsFormOpen)
        {
            CloseForm();
        }
    }

    [RelayCommand]
    private void ShowCurrent()
    {
        ShowCustomers = false;
        ShowArchive = false;
        ApplyFilter();
        UpdateSubtitle();
    }

    [RelayCommand]
    private void ShowArchiveTab()
    {
        ShowCustomers = false;
        ShowArchive = true;
        ApplyFilter();
        UpdateSubtitle();
    }

    [RelayCommand]
    private void ShowCustomersTab()
    {
        ShowArchive = false;
        ShowCustomers = true;
        ApplyFilter();
        UpdateSubtitle();
    }

    [RelayCommand]
    private void NewOrder()
    {
        ReloadOptions();

        if (!CanCreateOrder)
        {
            PageError = "Сначала добавьте рецепты в книгу.";
            return;
        }

        _suppressCustomerSearch = true;
        _editingOrderId = null;
        FormTitle = "Новый заказ";
        FormFirstName = string.Empty;
        FormLastName = string.Empty;
        FormPhone = string.Empty;
        FormDueDate = DateTimeOffset.Now.AddDays(1);
        FormComment = string.Empty;
        _paidManuallyEdited = false;
        _updatingPaidAutomatically = true;
        FormPaidAmount = 0m;
        _updatingPaidAutomatically = false;
        FormDiscountAmount = 0m;
        FormError = null;
        PageError = null;
        Lines.Clear();
        AddLine();
        UpdateTotal();
        _suppressCustomerSearch = false;
        ClearCustomerSuggestions();
        IsFormOpen = true;
    }

    public void EditOrder(OrderRowViewModel row) => EditOrder(row.Id);

    private void EditOrder(Guid orderId)
    {
        OrderDetails details;

        try
        {
            details = _orders.GetOrderDetails(orderId);
        }
        catch (ValidationException ex)
        {
            PageError = ex.Message;
            return;
        }

        ReloadOptions();

        foreach (var item in details.Order.Items)
        {
            if (item.RecipeId is not { } recipeId ||
                RecipeOptions.All(option => option.Id != recipeId))
            {
                PageError = "В заказе есть блюдо, которого нет в книге (удалено или в корзине). Восстановите рецепт.";
                return;
            }
        }

        _editingOrderId = orderId;
        _suppressCustomerSearch = true;
        FormTitle = $"Изменить заказ № {details.Order.Number}";
        FormFirstName = details.Order.FirstName;
        FormLastName = details.Order.LastName ?? string.Empty;
        FormPhone = details.Order.Phone;
        FormDueDate = new DateTimeOffset(details.Order.DueDate.ToDateTime(TimeOnly.MinValue));
        FormComment = details.Order.Comment ?? string.Empty;
        _paidManuallyEdited = true;
        _updatingPaidAutomatically = true;
        FormPaidAmount = details.Order.PaidAmount;
        _updatingPaidAutomatically = false;
        FormDiscountAmount = details.Order.DiscountAmount;
        FormError = null;
        PageError = null;
        _suppressCustomerSearch = false;
        ClearCustomerSuggestions();

        Lines.Clear();
        foreach (var item in details.Order.Items)
        {
            var option = RecipeOptions.FirstOrDefault(candidate => candidate.Id == item.RecipeId);
            if (option is null)
            {
                continue;
            }

            Lines.Add(new OrderLineViewModel(RecipeOptions, option, item.Quantity, RemoveLine, UpdateTotal));
        }

        UpdateTotal();
        IsDetailsOpen = false;
        IsFormOpen = true;
    }

    [RelayCommand]
    private void AddLine()
    {
        Lines.Add(new OrderLineViewModel(
            RecipeOptions,
            RecipeOptions.FirstOrDefault(),
            quantity: 1,
            RemoveLine,
            UpdateTotal));
        UpdateTotal();
    }

    private void RemoveLine(OrderLineViewModel line)
    {
        Lines.Remove(line);
        UpdateTotal();
    }

    [RelayCommand]
    private void CloseForm()
    {
        IsFormOpen = false;
        _editingOrderId = null;
        FormError = null;
        ClearCustomerSuggestions();
    }

    [RelayCommand]
    private void SaveOrder()
    {
        try
        {
            if (FormDueDate is null)
            {
                FormError = "Укажите дату выдачи.";
                return;
            }

            var lines = Lines
                .Where(line => line.Recipe is not null && line.Quantity.GetValueOrDefault() > 0m)
                .Select(line => (RecipeId: line.Recipe!.Id, Quantity: line.Quantity!.Value))
                .ToList();

            var dueDate = DateOnly.FromDateTime(FormDueDate.Value.Date);

            if (_editingOrderId is { } id)
            {
                _orders.UpdateOrder(id, FormFirstName, FormLastName, FormPhone, dueDate, FormComment, lines,
                    FormPaidAmount.GetValueOrDefault(), FormDiscountAmount.GetValueOrDefault());
            }
            else
            {
                _orders.CreateOrder(FormFirstName, FormLastName, FormPhone, dueDate, FormComment, lines,
                    FormPaidAmount.GetValueOrDefault(), FormDiscountAmount.GetValueOrDefault());
            }

            FormError = null;
            IsFormOpen = false;
            _editingOrderId = null;
            Reload();
            ReloadOptions();
            NotifyOrdersChanged();
        }
        catch (ValidationException ex)
        {
            FormError = ex.Message;
        }
    }

    public void Advance(OrderRowViewModel row)
    {
        try
        {
            _orders.AdvanceOrder(row.Id);
            PageError = null;
            Reload();
            RefreshDetails();
            NotifyOrdersChanged();
        }
        catch (ValidationException ex)
        {
            PageError = ex.Message;
        }
    }

    public void RequestCancel(OrderRowViewModel row)
    {
        _confirmAction = () =>
        {
            try
            {
                _orders.CancelOrder(row.Id);
                PageError = null;
                Reload();
                RefreshDetails();
                NotifyOrdersChanged();
            }
            catch (ValidationException ex)
            {
                PageError = ex.Message;
            }
        };

        ConfirmText = $"Отменить заказ № {row.NumberText}? Он уйдёт в архив.";
        ConfirmButtonText = "Отменить заказ";
        IsConfirmOpen = true;
    }

    [RelayCommand]
    private void SetStatusNew() => SetOrderStatus(OrderStatus.New);

    [RelayCommand]
    private void SetStatusCooking() => SetOrderStatus(OrderStatus.Cooking);

    [RelayCommand]
    private void SetStatusReady() => SetOrderStatus(OrderStatus.Ready);

    [RelayCommand]
    private void SetStatusDone() => SetOrderStatus(OrderStatus.Done);

    [RelayCommand]
    private void SetStatusCancelled() => SetOrderStatus(OrderStatus.Cancelled);

    private void SetOrderStatus(OrderStatus status)
    {
        if (_detailsOrderId is not { } id)
        {
            return;
        }

        try
        {
            _orders.SetOrderStatus(id, status);
            PageError = null;
            DetailsError = null;
            Reload();
            RefreshDetails();
            NotifyOrdersChanged();
        }
        catch (ValidationException ex)
        {
            DetailsError = ex.Message;
        }
    }

    private void NotifyOrdersChanged() => OrdersChanged?.Invoke();

    public void OpenDetails(OrderRowViewModel row) => OpenDetails(row.Id);

    public void OpenDetails(Guid orderId)
    {
        _detailsOrderId = orderId;
        DetailsError = null;
        ShoppingNotice = null;
        HasShoppingNotice = false;
        RefreshDetails();
        IsDetailsOpen = true;
    }

    public void OpenCustomerOrder(CustomerOrderRowViewModel row)
    {
        IsCustomerOpen = false;
        _customerDetailsId = null;
        OpenDetails(row.Id);
    }

    [RelayCommand]
    private void CloseDetails() => IsDetailsOpen = false;

    [RelayCommand]
    private void MarkOrderPaid()
    {
        if (_detailsOrderId is not { } id)
        {
            return;
        }

        try
        {
            _orders.MarkOrderPaid(id);
            DetailsError = null;
            Reload();
            RefreshDetails();
        }
        catch (ValidationException ex)
        {
            DetailsError = ex.Message;
        }
    }

    public void SetShoppingNotice(string message)
    {
        ShoppingNotice = message;
        HasShoppingNotice = true;
    }

    /// <summary>Текст списка покупок для копирования и печати.</summary>
    public string BuildShoppingText()
    {
        if (_detailsOrderId is not { } id)
        {
            return string.Empty;
        }

        var details = _orders.GetOrderDetails(id);
        var order = details.Order;
        var text = new System.Text.StringBuilder();

        text.AppendLine($"Заказ № {order.Number} · {order.ClientName} · {order.Phone}");
        text.AppendLine($"Выдача: {Formats.Date(order.DueDate)}");
        text.AppendLine();
        text.AppendLine("ЧТО КУПИТЬ");

        foreach (var group in details.ItemShopping)
        {
            text.AppendLine($"{group.Item.RecipeName} — {Formats.Quantity(group.Item.Quantity, group.Item.OutputUnit)}");

            if (group.Lines.Count == 0)
            {
                text.AppendLine("  состав недоступен");
            }
            else
            {
                foreach (var line in group.Lines)
                {
                    text.AppendLine($"  {line.IngredientName}: {Formats.Quantity(line.Amount, line.Unit)}");
                }
            }

            text.AppendLine();
        }

        if (order.Packaging.Count > 0)
        {
            text.AppendLine("УПАКОВКА");
            foreach (var box in order.Packaging)
            {
                var size = string.IsNullOrWhiteSpace(box.Size) ? string.Empty : $" ({box.Size})";
                text.AppendLine($"  {box.Name}{size} × {box.Quantity}");
            }

            text.AppendLine();
        }

        text.AppendLine($"Итого на закупку: {Formats.Money(details.ShoppingCost)}");
        return text.ToString();
    }

    [RelayCommand]
    private void AddPackaging()
    {
        if (_detailsOrderId is not { } id || PackagingToAdd is null)
        {
            return;
        }

        var quantity = (int)Math.Round(PackagingQuantity.GetValueOrDefault());
        if (quantity <= 0)
        {
            DetailsError = "Укажите количество коробок.";
            return;
        }

        var lines = PackagingLines();
        lines[PackagingToAdd.Id] = lines.GetValueOrDefault(PackagingToAdd.Id) + quantity;
        ApplyPackaging(id, lines);
    }

    [RelayCommand]
    private void RemovePackaging(OrderPackagingRowViewModel row)
    {
        RemovePackagingInternal(row);
    }

    public void RemovePackagingInternal(OrderPackagingRowViewModel row)
    {
        if (_detailsOrderId is not { } id)
        {
            return;
        }

        var lines = PackagingLines();
        if (row.PackagingId is { } packagingId)
        {
            lines.Remove(packagingId);
        }

        ApplyPackaging(id, lines);
    }

    [RelayCommand]
    private void ClearPackaging()
    {
        if (_detailsOrderId is not { } id)
        {
            return;
        }

        ApplyPackaging(id, new Dictionary<Guid, int>());
    }

    private Dictionary<Guid, int> PackagingLines()
    {
        var lines = new Dictionary<Guid, int>();
        foreach (var row in DetailsPackaging)
        {
            if (row.PackagingId is { } packagingId)
            {
                lines[packagingId] = lines.GetValueOrDefault(packagingId) + row.Model.Quantity;
            }
        }

        return lines;
    }

    private void ApplyPackaging(Guid orderId, Dictionary<Guid, int> lines)
    {
        try
        {
            _orders.SetOrderPackaging(orderId, lines.Select(pair => (pair.Key, pair.Value)));
            DetailsError = null;
            PackagingQuantity = 1;
            Reload();
            RefreshDetails();
        }
        catch (ValidationException ex)
        {
            DetailsError = ex.Message;
        }
    }

    [RelayCommand]
    private void ConfirmCancel()
    {
        IsConfirmOpen = false;
        _confirmAction?.Invoke();
        _confirmAction = null;
    }

    [RelayCommand]
    private void CloseConfirm()
    {
        IsConfirmOpen = false;
        _confirmAction = null;
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnFormPhoneChanged(string value) => RefreshCustomerSuggestions();

    partial void OnFormLastNameChanged(string value) => RefreshCustomerSuggestions();

    public void ApplyCustomer(CustomerSuggestionViewModel suggestion)
    {
        _suppressCustomerSearch = true;
        FormFirstName = suggestion.Customer.FirstName;
        FormLastName = suggestion.Customer.LastName ?? string.Empty;
        FormPhone = suggestion.Customer.Phone;
        _suppressCustomerSearch = false;
        ClearCustomerSuggestions();
    }

    private void RefreshCustomerSuggestions()
    {
        if (_suppressCustomerSearch || !IsFormOpen)
        {
            return;
        }

        var phone = FormPhone.Trim();
        var lastName = FormLastName.Trim();
        var query = phone.Count(char.IsDigit) >= 3
            ? phone
            : lastName.Length >= 2
                ? lastName
                : null;

        CustomerSuggestions.Clear();

        if (query is not null)
        {
            foreach (var customer in _customers.Search(query, 5))
            {
                CustomerSuggestions.Add(new CustomerSuggestionViewModel(customer, this));
            }
        }

        OnPropertyChanged(nameof(HasCustomerSuggestions));
    }

    private void ClearCustomerSuggestions()
    {
        CustomerSuggestions.Clear();
        OnPropertyChanged(nameof(HasCustomerSuggestions));
    }

    partial void OnShowArchiveChanged(bool value)
    {
        OnPropertyChanged(nameof(CurrentTabActive));
        OnPropertyChanged(nameof(ArchiveTabActive));
        OnPropertyChanged(nameof(CustomersTabActive));
        OnPropertyChanged(nameof(EmptyText));
    }

    partial void OnShowCustomersChanged(bool value)
    {
        OnPropertyChanged(nameof(CurrentTabActive));
        OnPropertyChanged(nameof(ArchiveTabActive));
        OnPropertyChanged(nameof(CustomersTabActive));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(CustomerEmptyText));
    }

    private void UpdateTotal()
    {
        var baseTotal = Lines
            .Where(line => line.Recipe is not null && line.Quantity.GetValueOrDefault() > 0m)
            .Sum(line => decimal.Round(
                line.Recipe!.UnitPrice * line.Quantity.GetValueOrDefault(),
                2,
                MidpointRounding.AwayFromZero));

        var discount = Math.Max(0m, FormDiscountAmount.GetValueOrDefault());
        var total = Math.Ceiling(Math.Max(0m, baseTotal - discount));

        FormBaseTotalText = Formats.MoneySmart(baseTotal);
        FormHasDiscount = discount > 0m;
        FormTotalText = Formats.MoneySmart(total);

        if (!_paidManuallyEdited)
        {
            _updatingPaidAutomatically = true;
            FormPaidAmount = total <= 0m ? 0m : Math.Ceiling(total / 2m);
            _updatingPaidAutomatically = false;
        }

        var paid = FormPaidAmount.GetValueOrDefault();
        var remaining = Math.Max(0m, total - paid);
        FormRemainingText = Formats.MoneySmart(remaining);
    }

    partial void OnFormPaidAmountChanged(decimal? value)
    {
        if (_updatingPaidAutomatically)
        {
            return;
        }

        _paidManuallyEdited = true;
        UpdateTotal();
    }

    partial void OnFormDiscountAmountChanged(decimal? value) => UpdateTotal();

    private void RefreshDetails()
    {
        if (_detailsOrderId is not { } id)
        {
            return;
        }

        try
        {
            var details = _orders.GetOrderDetails(id);
            var order = details.Order;

            DetailsNumber = "№ " + order.Number;
            DetailsClient = order.ClientName;
            DetailsPhone = order.Phone;
            var customer = order.CustomerId is { } customerId
                ? _customers.GetCustomers().FirstOrDefault(item => item.Id == customerId)
                : null;
            DetailsCustomer = customer is null
                ? "Клиент не привязан к карточке"
                : $"Клиент № {customer.Number}";
            DetailsCreated = "Создан: " + Formats.Moment(order.CreatedAt);
            DetailsDue = "Выдача: " + Formats.Date(order.DueDate);
            DetailsStatus = Formats.OrderStatusText(order.Status);
            DetailsStatusBrush = OrderRowViewModel.CreateStatusBrush(order.Status);
            DetailsIsNew = order.Status == OrderStatus.New;
            DetailsIsCooking = order.Status == OrderStatus.Cooking;
            DetailsIsReady = order.Status == OrderStatus.Ready;
            DetailsIsDone = order.Status == OrderStatus.Done;
            DetailsIsCancelled = order.Status == OrderStatus.Cancelled;
            DetailsComment = order.Comment ?? string.Empty;
            DetailsHasComment = !string.IsNullOrWhiteSpace(order.Comment);
            DetailsTotal = Formats.MoneySmart(order.TotalPrice);
            DetailsDiscountText = Formats.MoneySmart(order.DiscountAmount);
            DetailsHasDiscount = order.DiscountAmount > 0m;
            DetailsPaidText = Formats.MoneySmart(order.PaidAmount);
            DetailsRemainingText = Formats.MoneySmart(order.RemainingAmount);
            DetailsIsPaid = order.IsPaid;
            CanMarkPaid = !order.IsPaid && order.Status != OrderStatus.Cancelled;
            DetailsShoppingCost = Formats.Money(details.ShoppingCost);

            DetailsItems.Clear();
            foreach (var item in order.Items)
            {
                DetailsItems.Add(new OrderItemDetailRowViewModel(item));
            }

            DetailsShoppingGroups.Clear();
            foreach (var group in details.ItemShopping)
            {
                DetailsShoppingGroups.Add(new OrderShoppingGroupViewModel(group));
            }

            CanEditPackaging = !OrderStatuses.IsFinal(order.Status);
            DetailsPackaging.Clear();
            foreach (var box in order.Packaging)
            {
                DetailsPackaging.Add(new OrderPackagingRowViewModel(box, this, CanEditPackaging));
            }

            DetailsPackagingTotal = Formats.Money(order.PackagingTotal);
            HasPackagingNote = order.PackagingTotal > 0m;
            DetailsPackagingNote = HasPackagingNote
                ? "включая упаковку " + Formats.Money(order.PackagingTotal)
                : string.Empty;

            HasDetailsMarkup = order.TotalCost > 0m;
            DetailsMarkupText = HasDetailsMarkup
                ? "наценка " + Math.Round((order.TotalPrice / order.TotalCost - 1m) * 100m) + "%"
                : string.Empty;

            OnPropertyChanged(nameof(DetailsShoppingEmpty));
            OnPropertyChanged(nameof(DetailsPackagingEmpty));
        }
        catch (ValidationException)
        {
            IsDetailsOpen = false;
            _detailsOrderId = null;
        }
    }

    private void Reload()
    {
        _allOrders = _orders.GetOrders().ToList();
        ApplyFilter();
        UpdateSubtitle();

        if (IsCustomerOpen && _customerDetailsId is { } customerId)
        {
            OpenCustomer(customerId);
        }
    }

    private void ReloadOptions()
    {
        RecipeOptions.Clear();

        foreach (var summary in _recipes.GetRecipes())
        {
            RecipeOptions.Add(new OrderRecipeOption(
                summary.Recipe.Id,
                summary.Recipe.Name,
                summary.PricePerUnit,
                summary.Recipe.OutputQuantity,
                summary.Recipe.OutputUnit));
        }

        ReloadPackagingOptions();

        OnPropertyChanged(nameof(CanCreateOrder));
        OnPropertyChanged(nameof(CreateTooltip));
    }

    private void ReloadPackagingOptions()
    {
        var selectedId = PackagingToAdd?.Id;
        PackagingOptions.Clear();

        foreach (var summary in _packaging.GetItems())
        {
            PackagingOptions.Add(new PackagingOption(summary));
        }

        PackagingToAdd = PackagingOptions.FirstOrDefault(option => option.Id == selectedId);
    }

    private void ApplyCustomerFilter()
    {
        var query = SearchText.Trim();
        var ordersByCustomer = _allOrders
            .Where(order => order.CustomerId is not null)
            .GroupBy(order => order.CustomerId!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());

        CustomerRows.Clear();

        foreach (var customer in _customers.GetCustomers())
        {
            if (query.Length > 0 &&
                !customer.FullName.Contains(query, StringComparison.CurrentCultureIgnoreCase) &&
                !customer.Phone.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !customer.Number.ToString().Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ordersByCustomer.TryGetValue(customer.Id, out var orders);
            orders ??= new List<Order>();

            var revenue = orders
                .Where(order => order.Status != OrderStatus.Cancelled)
                .Sum(order => order.TotalPrice);
            var last = orders.Count == 0 ? (DateOnly?)null : orders.Max(order => order.DueDate);

            CustomerRows.Add(new CustomerListRowViewModel(customer, orders.Count, revenue, last, this));
        }

        OnPropertyChanged(nameof(IsCustomerListEmpty));
        OnPropertyChanged(nameof(CustomerEmptyText));
    }

    public void OpenCustomer(CustomerListRowViewModel row) => OpenCustomer(row.Id);

    private void OpenCustomer(Guid customerId)
    {
        var customer = _customers.GetCustomers().FirstOrDefault(item => item.Id == customerId);
        if (customer is null)
        {
            return;
        }

        _customerDetailsId = customerId;
        var orders = OrdersForCustomer(customerId);

        CustomerTitle = $"№ {customer.Number} · {customer.FullName}";
        CustomerPhone = customer.Phone;

        var revenue = orders
            .Where(order => order.Status != OrderStatus.Cancelled)
            .Sum(order => order.TotalPrice);

        CustomerStats = orders.Count == 0
            ? "Заказов пока нет"
            : $"Заказов: {orders.Count} · на сумму {Formats.MoneySmart(revenue)}" +
              (orders.Count > 0 ? $" · последний {Formats.Date(orders.Max(order => order.DueDate))}" : string.Empty);

        CustomerOrders.Clear();
        foreach (var order in orders.OrderByDescending(order => order.Number))
        {
            CustomerOrders.Add(new CustomerOrderRowViewModel(order, this));
        }

        CanRepeatCustomerOrder = orders.Any(order =>
            order.Items.Count > 0 &&
            order.Items.All(item => item.RecipeId is { } recipeId &&
                                    RecipeOptions.Any(option => option.Id == recipeId)));

        CustomerError = null;
        IsCustomerOpen = true;
        OnPropertyChanged(nameof(CustomerOrdersEmpty));
    }

    [RelayCommand]
    private void CloseCustomer()
    {
        IsCustomerOpen = false;
        _customerDetailsId = null;
    }

    [RelayCommand]
    private void DeleteCustomer()
    {
        if (_customerDetailsId is not { } id)
        {
            return;
        }

        var customer = _customers.GetCustomers().FirstOrDefault(item => item.Id == id);
        if (customer is null)
        {
            return;
        }

        var orderCount = OrdersForCustomer(id).Count;

        _confirmAction = () =>
        {
            try
            {
                _customers.DeleteCustomer(id);
                CustomerError = null;
                PageError = null;
                IsCustomerOpen = false;
                _customerDetailsId = null;
                Reload();
            }
            catch (ValidationException ex)
            {
                CustomerError = ex.Message;
            }
        };

        ConfirmText = orderCount == 0
            ? $"Удалить клиента № {customer.Number} «{customer.FullName}»? Заказов у него нет, данные будут удалены."
            : $"Удалить клиента № {customer.Number} «{customer.FullName}»? " +
              $"{orderCount} {Formats.Plural(orderCount, "заказ", "заказа", "заказов")} останутся в истории, " +
              "но потеряют привязку к карточке. При новом заказе с тем же телефоном клиент появится снова.";
        ConfirmButtonText = "Удалить клиента";
        IsConfirmOpen = true;
    }

    [RelayCommand]
    private void NewOrderForCustomer()
    {
        if (_customerDetailsId is not { } id)
        {
            return;
        }

        var customer = _customers.GetCustomers().FirstOrDefault(item => item.Id == id);
        if (customer is null)
        {
            return;
        }

        NewOrderCommand.Execute(null);
        if (!IsFormOpen)
        {
            return;
        }

        _suppressCustomerSearch = true;
        FormFirstName = customer.FirstName;
        FormLastName = customer.LastName ?? string.Empty;
        FormPhone = customer.Phone;
        _suppressCustomerSearch = false;
        ClearCustomerSuggestions();
        IsCustomerOpen = false;
    }

    [RelayCommand]
    private void RepeatLastOrder()
    {
        if (_customerDetailsId is not { } id)
        {
            return;
        }

        var customer = _customers.GetCustomers().FirstOrDefault(item => item.Id == id);
        if (customer is null)
        {
            return;
        }

        var last = OrdersForCustomer(id)
            .Where(order => order.Items.Count > 0)
            .OrderByDescending(order => order.Number)
            .FirstOrDefault();

        if (last is null)
        {
            CustomerError = "У клиента пока нет заказов, которые можно повторить.";
            return;
        }

        foreach (var item in last.Items)
        {
            if (item.RecipeId is not { } recipeId ||
                RecipeOptions.All(option => option.Id != recipeId))
            {
                CustomerError = "В последнем заказе есть блюдо, которого нет в книге. " +
                                "Восстановите рецепт или создайте заказ вручную.";
                return;
            }
        }

        NewOrderCommand.Execute(null);
        if (!IsFormOpen)
        {
            return;
        }

        _suppressCustomerSearch = true;
        FormFirstName = customer.FirstName;
        FormLastName = customer.LastName ?? string.Empty;
        FormPhone = customer.Phone;
        FormDueDate = DateTimeOffset.Now.AddDays(1);
        FormComment = $"Повтор заказа № {last.Number}";

        Lines.Clear();
        foreach (var item in last.Items)
        {
            var option = RecipeOptions.First(option => option.Id == item.RecipeId);
            Lines.Add(new OrderLineViewModel(RecipeOptions, option, item.Quantity, RemoveLine, UpdateTotal));
        }

        _suppressCustomerSearch = false;
        UpdateTotal();
        ClearCustomerSuggestions();
        IsCustomerOpen = false;
    }

    private List<Order> OrdersForCustomer(Guid customerId) =>
        _allOrders.Where(order => order.CustomerId == customerId).ToList();

    private void ApplyFilter()
    {
        if (ShowCustomers)
        {
            ApplyCustomerFilter();
            return;
        }

        var query = SearchText.Trim();

        var filtered = _allOrders.Where(order =>
        {
            var archived = OrderStatuses.IsFinal(order.Status);
            if (archived != ShowArchive)
            {
                return false;
            }

            if (query.Length == 0)
            {
                return true;
            }

            return order.Number.ToString().Contains(query, StringComparison.OrdinalIgnoreCase) ||
                   order.ClientName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                   order.Phone.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                   order.Items.Any(item => item.RecipeName.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        });

        filtered = ShowArchive
            ? filtered.OrderByDescending(order => order.Number)
            : filtered.OrderBy(order => order.DueDate).ThenBy(order => order.Number);

        Items.Clear();
        foreach (var order in filtered)
        {
            Items.Add(new OrderRowViewModel(order, this));
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyText));
    }

    private void UpdateSubtitle()
    {
        if (ShowCustomers)
        {
            var customerCount = _customers.GetCustomers().Count;
            Subtitle = customerCount == 0
                ? "Пока нет клиентов"
                : $"{customerCount} {Formats.Plural(customerCount, "клиент", "клиента", "клиентов")} · " +
                  "история заказов и повтор";
            return;
        }

        var current = _allOrders.Count(order => !OrderStatuses.IsFinal(order.Status));
        var archived = _allOrders.Count - current;

        Subtitle = _allOrders.Count == 0
            ? "Пока нет заказов — создайте первый"
            : $"{current} {Formats.Plural(current, "текущий", "текущих", "текущих")} · {archived} в архиве";
    }
}

/// <summary>Вариант блюда в заказе.</summary>
public sealed class OrderRecipeOption
{
    public OrderRecipeOption(Guid id, string name, decimal unitPrice, decimal outputQuantity, string outputUnit)
    {
        Id = id;
        Name = name;
        UnitPrice = unitPrice;
        OutputQuantity = outputQuantity;
        OutputUnit = outputUnit;
    }

    public Guid Id { get; }

    public string Name { get; }

    public decimal UnitPrice { get; }

    public decimal OutputQuantity { get; }

    public string OutputUnit { get; }

    public override string ToString() => Name;
}

/// <summary>Строка состава в форме заказа.</summary>
public sealed partial class OrderLineViewModel : ViewModelBase
{
    private readonly Action<OrderLineViewModel> _remove;
    private readonly Action _changed;

    public OrderLineViewModel(
        IEnumerable<OrderRecipeOption> options,
        OrderRecipeOption? recipe,
        decimal quantity,
        Action<OrderLineViewModel> remove,
        Action changed)
    {
        Options = options.ToList();
        _recipe = recipe ?? Options.FirstOrDefault();
        _quantity = quantity;
        _remove = remove;
        _changed = changed;
    }

    public IReadOnlyList<OrderRecipeOption> Options { get; }

    [ObservableProperty]
    private OrderRecipeOption? _recipe;

    [ObservableProperty]
    private decimal? _quantity = 1;

    public bool IsWeight => Recipe is not null && RecipeUnits.IsWeight(Recipe.OutputUnit);

    public string UnitText => Recipe?.OutputUnit ?? string.Empty;

    public decimal Minimum => IsWeight ? 0.001m : 1m;

    public decimal Increment => Recipe?.OutputUnit switch
    {
        RecipeUnits.Kilograms => 0.1m,
        RecipeUnits.Grams => 10m,
        _ => 1m,
    };

    public string QuantityFormat => IsWeight ? "0.###" : "0";

    public string LineTotalText => Recipe is null
        ? "—"
        : Formats.MoneySmart(decimal.Round(
            Recipe.UnitPrice * Math.Max(0m, Quantity.GetValueOrDefault()),
            2,
            MidpointRounding.AwayFromZero));

    [RelayCommand]
    private void Remove() => _remove(this);

    partial void OnRecipeChanged(OrderRecipeOption? value)
    {
        if (!RecipeUnits.IsWeight(value?.OutputUnit) && Quantity is { } quantity &&
            quantity != Math.Truncate(quantity))
        {
            Quantity = Math.Max(1m, Math.Ceiling(quantity));
        }

        OnPropertyChanged(nameof(IsWeight));
        OnPropertyChanged(nameof(UnitText));
        OnPropertyChanged(nameof(Minimum));
        OnPropertyChanged(nameof(Increment));
        OnPropertyChanged(nameof(QuantityFormat));
        OnPropertyChanged(nameof(LineTotalText));
        _changed();
    }

    partial void OnQuantityChanged(decimal? value)
    {
        OnPropertyChanged(nameof(LineTotalText));
        _changed();
    }
}

/// <summary>Строка списка заказов.</summary>
public sealed class OrderRowViewModel : ViewModelBase
{
    public OrderRowViewModel(Order order, OrdersViewModel owner)
    {
        Id = order.Id;
        NumberText = order.Number.ToString();
        CreatedText = Formats.Moment(order.CreatedAt);
        ClientName = order.ClientName;
        Phone = order.Phone;
        DueText = Formats.Date(order.DueDate);
        StatusText = Formats.OrderStatusText(order.Status);
        StatusBrush = CreateStatusBrush(order.Status);
        TotalText = Formats.MoneySmart(order.TotalPrice);

        if (order.Status == OrderStatus.Cancelled)
        {
            ShowPayment = false;
        }
        else if (order.IsPaid)
        {
            ShowPayment = order.PaidAmount > 0m;
            PaymentText = "оплачен";
            PaymentBrush = new SolidColorBrush(Color.Parse("#4F7A4A"));
        }
        else
        {
            ShowPayment = true;
            PaymentText = "долг " + Formats.MoneySmart(order.RemainingAmount);
            PaymentBrush = new SolidColorBrush(Color.Parse("#A5433A"));
        }

        ItemsText = string.Join(", ", order.Items.Select(item =>
            $"{item.RecipeName} × {Formats.Quantity(item.Quantity, item.OutputUnit)}"));

        CanAdvance = !OrderStatuses.IsFinal(order.Status);
        CanEdit = !OrderStatuses.IsFinal(order.Status);
        AdvanceText = order.Status switch
        {
            OrderStatus.New => "В работу",
            OrderStatus.Cooking => "Готов",
            OrderStatus.Ready => "Выдан",
            _ => string.Empty,
        };

        DetailsCommand = new RelayCommand(() => owner.OpenDetails(this));
        EditCommand = new RelayCommand(() => owner.EditOrder(this), () => CanEdit);
        AdvanceCommand = new RelayCommand(() => owner.Advance(this), () => CanAdvance);
        CancelCommand = new RelayCommand(() => owner.RequestCancel(this), () => CanAdvance);
    }

    public Guid Id { get; }

    public string NumberText { get; }

    public string CreatedText { get; }

    public string ClientName { get; }

    public string Phone { get; }

    public string DueText { get; }

    public string StatusText { get; }

    public IBrush StatusBrush { get; }

    public string TotalText { get; }

    public bool ShowPayment { get; }

    public string PaymentText { get; } = string.Empty;

    public IBrush PaymentBrush { get; } = Brushes.Gray;

    public string ItemsText { get; }

    public bool CanAdvance { get; }

    public bool CanEdit { get; }

    public string AdvanceText { get; }

    public IRelayCommand DetailsCommand { get; }

    public IRelayCommand EditCommand { get; }

    public IRelayCommand AdvanceCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public static IBrush CreateStatusBrush(OrderStatus status) => new SolidColorBrush(Color.Parse(status switch
    {
        OrderStatus.New => "#2F80ED",
        OrderStatus.Cooking => "#C98A00",
        OrderStatus.Ready => "#2E9E4F",
        OrderStatus.Done => "#7A7A5A",
        OrderStatus.Cancelled => "#C0392B",
        _ => "#7A7A5A",
    }));
}

/// <summary>Строка состава в деталях заказа.</summary>
public sealed class OrderItemDetailRowViewModel : ViewModelBase
{
    public OrderItemDetailRowViewModel(OrderItem item)
    {
        Name = item.RecipeName;
        QuantityText = "× " + Formats.Quantity(item.Quantity, item.OutputUnit);
        UnitPriceText = Formats.UnitPrice(item.UnitPrice, item.OutputUnit);
        TotalText = Formats.MoneySmart(decimal.Round(item.UnitPrice * item.Quantity, 2, MidpointRounding.AwayFromZero));
    }

    public string Name { get; }

    public string QuantityText { get; }

    public string UnitPriceText { get; }

    public string TotalText { get; }
}

/// <summary>Строка списка покупок под заказ.</summary>
public sealed partial class ShoppingRowViewModel : ViewModelBase
{
    public ShoppingRowViewModel(ShoppingListItem item)
    {
        Name = item.IngredientName;
        AmountText = Formats.Quantity(item.Amount, item.Unit);
        CostText = Formats.Money(item.EstimatedCost);
    }

    public string Name { get; }

    public string AmountText { get; }

    public string CostText { get; }

    [ObservableProperty]
    private bool _isChecked;

    public double RowOpacity => IsChecked ? 0.45 : 1.0;

    partial void OnIsCheckedChanged(bool value) => OnPropertyChanged(nameof(RowOpacity));
}

/// <summary>Ингредиенты для одной позиции заказа.</summary>
public sealed class OrderShoppingGroupViewModel : ViewModelBase
{
    public OrderShoppingGroupViewModel(OrderItemShopping shopping)
    {
        Name = shopping.Item.RecipeName;
        QuantityText = "× " + Formats.Quantity(shopping.Item.Quantity, shopping.Item.OutputUnit);
        CostText = Formats.Money(shopping.Cost);
        Lines = shopping.Lines.Select(line => new ShoppingRowViewModel(line)).ToList();
        IsEmpty = Lines.Count == 0;
    }

    public string Name { get; }

    public string QuantityText { get; }

    public string CostText { get; }

    public IReadOnlyList<ShoppingRowViewModel> Lines { get; }

    public bool IsEmpty { get; }
}

/// <summary>Клиент из базы для автозаполнения заказа.</summary>
public sealed class CustomerSuggestionViewModel : ViewModelBase
{
    public CustomerSuggestionViewModel(Customer customer, OrdersViewModel owner)
    {
        Customer = customer;
        NumberText = "#" + customer.Number;
        Name = customer.FullName;
        Phone = customer.Phone;
        FillCommand = new RelayCommand(() => owner.ApplyCustomer(this));
    }

    public Customer Customer { get; }

    public string NumberText { get; }

    public string Name { get; }

    public string Phone { get; }

    public IRelayCommand FillCommand { get; }
}

/// <summary>Клиент в списке вкладки «Клиенты».</summary>
public sealed class CustomerListRowViewModel : ViewModelBase
{
    public CustomerListRowViewModel(
        Customer customer,
        int ordersCount,
        decimal revenue,
        DateOnly? lastOrder,
        OrdersViewModel owner)
    {
        Id = customer.Id;
        NumberText = "#" + customer.Number;
        Name = customer.FullName;
        Phone = customer.Phone;
        OrdersText = ordersCount.ToString();
        RevenueText = Formats.MoneySmart(revenue);
        LastOrderText = lastOrder is { } date ? Formats.Date(date) : "—";
        OpenCommand = new RelayCommand(() => owner.OpenCustomer(this));
    }

    public Guid Id { get; }

    public string NumberText { get; }

    public string Name { get; }

    public string Phone { get; }

    public string OrdersText { get; }

    public string RevenueText { get; }

    public string LastOrderText { get; }

    public IRelayCommand OpenCommand { get; }
}

/// <summary>Заказ в карточке клиента.</summary>
public sealed class CustomerOrderRowViewModel : ViewModelBase
{
    public CustomerOrderRowViewModel(Order order, OrdersViewModel owner)
    {
        Id = order.Id;
        NumberText = "№ " + order.Number;
        DueText = Formats.Date(order.DueDate);
        StatusText = Formats.OrderStatusText(order.Status);
        StatusBrush = OrderRowViewModel.CreateStatusBrush(order.Status);
        TotalText = Formats.MoneySmart(order.TotalPrice);

        PaymentText = order.Status == OrderStatus.Cancelled
            ? (order.PaidAmount > 0m ? "оплачено " + Formats.MoneySmart(order.PaidAmount) : "без оплаты")
            : order.IsPaid
                ? "оплачено полностью"
                : order.PaidAmount > 0m
                    ? $"оплачено {Formats.MoneySmart(order.PaidAmount)} · остаток {Formats.MoneySmart(order.RemainingAmount)}"
                    : $"остаток {Formats.MoneySmart(order.RemainingAmount)}";

        OpenCommand = new RelayCommand(() => owner.OpenCustomerOrder(this));
    }

    public Guid Id { get; }

    public string NumberText { get; }

    public string DueText { get; }

    public string StatusText { get; }

    public IBrush StatusBrush { get; }

    public string TotalText { get; }

    public string PaymentText { get; }

    public IRelayCommand OpenCommand { get; }
}

/// <summary>Вариант упаковки для выбора в заказе.</summary>
public sealed class PackagingOption
{
    public PackagingOption(PackagingSummary summary)
    {
        Id = summary.Item.Id;
        Name = summary.Item.Name;

        var parts = new List<string> { Name };
        if (!string.IsNullOrWhiteSpace(summary.Item.Size))
        {
            parts.Add(summary.Item.Size!);
        }

        if (!string.IsNullOrWhiteSpace(summary.CategoryName))
        {
            parts.Add(summary.CategoryName!);
        }

        parts.Add(Formats.Money(summary.Item.Price));
        Display = string.Join(" · ", parts);
    }

    public Guid Id { get; }

    public string Name { get; }

    public string Display { get; }

    public override string ToString() => Display;
}

/// <summary>Выбранная упаковка в деталях заказа.</summary>
public sealed class OrderPackagingRowViewModel : ViewModelBase
{
    public OrderPackagingRowViewModel(OrderPackaging box, OrdersViewModel owner, bool canRemove)
    {
        Model = box;
        PackagingId = box.PackagingId;
        Name = box.Name;
        SizeText = box.Size ?? string.Empty;
        HasSize = SizeText.Length > 0;
        QuantityText = "× " + box.Quantity;
        UnitPriceText = Formats.Money(box.SalePrice);
        TotalText = Formats.Money(box.SaleTotal);
        CanRemove = canRemove;
        RemoveCommand = new RelayCommand(() => owner.RemovePackagingInternal(this), () => CanRemove);
    }

    public OrderPackaging Model { get; }

    public Guid? PackagingId { get; }

    public string Name { get; }

    public string SizeText { get; }

    public bool HasSize { get; }

    public string QuantityText { get; }

    public string UnitPriceText { get; }

    public string TotalText { get; }

    public bool CanRemove { get; }

    public IRelayCommand RemoveCommand { get; }
}
