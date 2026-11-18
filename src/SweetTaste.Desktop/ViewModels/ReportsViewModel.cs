using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SweetTaste.Core.Domain;
using SweetTaste.Core.Services;

namespace SweetTaste.Desktop.ViewModels;
/// <summary>Отчёты: период, выручка, прибыль, топ позиций и разбивка по дням.</summary>
public sealed partial class ReportsViewModel : ViewModelBase
{
    private enum Preset
    {
        Week,
        Month,
        Year,
        Custom,
    }

    private readonly ReportService _reports;
    private Preset _preset = Preset.Month;
    private bool _suppressRefresh;
    private Report? _currentReport;

    public ReportsViewModel(ReportService reports)
    {
        _reports = reports;
        ApplyPreset(Preset.Month);
    }

    public ObservableCollection<ReportTopRowViewModel> TopItems { get; } = new();

    public ObservableCollection<ReportDayRowViewModel> Days { get; } = new();

    public ObservableCollection<ReportCustomerRowViewModel> Customers { get; } = new();

    public ObservableCollection<CustomerChartRowViewModel> CustomerChart { get; } = new();

    [ObservableProperty]
    private DateTimeOffset? _fromDate;

    [ObservableProperty]
    private DateTimeOffset? _toDate;

    [ObservableProperty]
    private string _periodText = string.Empty;

    [ObservableProperty]
    private string _revenueText = "0 ₽";

    [ObservableProperty]
    private string _profitText = "0 ₽";

    [ObservableProperty]
    private IBrush _profitBrush = ThemeBrush("SuccessBrush");

    [ObservableProperty]
    private string _profitHint = "после себестоимости";

    [ObservableProperty]
    private string _ordersText = "0";

    [ObservableProperty]
    private string _ordersHint = "выдано за период";

    [ObservableProperty]
    private string _averageCheckText = "0 ₽";

    [ObservableProperty]
    private string _purchasesText = "0 ₽";

    [ObservableProperty]
    private string _purchasesHint = "закупки за период";

    [ObservableProperty]
    private bool _hasEstimatedCost;

    [ObservableProperty]
    private string _inProgressText = string.Empty;

    [ObservableProperty]
    private bool _hasInProgress;

    [ObservableProperty]
    private bool _showCustomers;

    [ObservableProperty]
    private string _customersSummary = string.Empty;

    [ObservableProperty]
    private bool _isCustomersEmpty;

    [ObservableProperty]
    private string? _pageError;

    public bool OverviewTabActive => !ShowCustomers;

    public bool CustomersTabActive => ShowCustomers;

    public bool WeekActive => _preset == Preset.Week;

    public bool MonthActive => _preset == Preset.Month;

    public bool YearActive => _preset == Preset.Year;

    public bool IsTopEmpty => TopItems.Count == 0;

    public bool IsDaysEmpty => Days.Count == 0;

    [RelayCommand]
    private void UseWeek() => ApplyPreset(Preset.Week);

    [RelayCommand]
    private void UseMonth() => ApplyPreset(Preset.Month);

    [RelayCommand]
    private void UseYear() => ApplyPreset(Preset.Year);

    [RelayCommand]
    private void ShowOverviewTab()
    {
        ShowCustomers = false;
        NotifyTabs();
    }

    [RelayCommand]
    private void ShowCustomersTab()
    {
        ShowCustomers = true;
        NotifyTabs();
    }

    private void NotifyTabs()
    {
        OnPropertyChanged(nameof(OverviewTabActive));
        OnPropertyChanged(nameof(CustomersTabActive));
    }

    partial void OnFromDateChanged(DateTimeOffset? value) => OnPeriodDateChanged();

    partial void OnToDateChanged(DateTimeOffset? value) => OnPeriodDateChanged();

    private void OnPeriodDateChanged()
    {
        if (_suppressRefresh)
        {
            return;
        }

        _preset = Preset.Custom;
        Refresh();
        NotifyPresets();
    }

    private void ApplyPreset(Preset preset)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var (from, to) = preset switch
        {
            Preset.Week => (today.AddDays(-6), today),
            Preset.Year => (new DateOnly(today.Year, 1, 1), new DateOnly(today.Year, 12, 31)),
            _ => (new DateOnly(today.Year, today.Month, 1), new DateOnly(today.Year, today.Month, 1)
                .AddMonths(1).AddDays(-1)),
        };

        _preset = preset;
        _suppressRefresh = true;
        FromDate = ToDateTimeOffset(from);
        ToDate = ToDateTimeOffset(to);
        _suppressRefresh = false;

        Refresh();
        NotifyPresets();
    }

    private void Refresh()
    {
        if (FromDate is null || ToDate is null)
        {
            PageError = "Укажите начало и конец периода.";
            return;
        }

        var from = DateOnly.FromDateTime(FromDate.Value.Date);
        var to = DateOnly.FromDateTime(ToDate.Value.Date);

        Report report;
        try
        {
            report = _reports.Build(from, to);
        }
        catch (ValidationException ex)
        {
            PageError = ex.Message;
            return;
        }

        PageError = null;
        _currentReport = report;
        PeriodText = from == to
            ? "По дате выдачи · " + Formats.Date(from)
            : $"По дате выдачи · {Formats.Date(from)} — {Formats.Date(to)}";

        RevenueText = Formats.MoneySmart(report.Revenue);
        ProfitText = Formats.MoneySmart(report.Profit);
        ProfitBrush = ThemeBrush(report.Profit < 0m ? "DangerBrush" : "SuccessBrush");
        ProfitHint = report.Revenue > 0m
            ? "маржа " + Math.Round(report.Profit / report.Revenue * 100m) + "%"
            : "после себестоимости";

        OrdersText = report.OrdersCount.ToString();
        OrdersHint = report.OrdersCount == 0
            ? "выдано за период"
            : $"{Formats.Plural(report.OrdersCount, "заказ", "заказа", "заказов")} выдано";

        AverageCheckText = Formats.MoneySmart(report.AverageCheck);
        PurchasesText = Formats.MoneySmart(report.PurchasesCost);
        PurchasesHint = $"{Formats.Plural(report.PurchasesCount, "закупка", "закупки", "закупок")} за период";

        HasEstimatedCost = report.HasEstimatedCost;
        HasInProgress = report.InProgressCount > 0;
        InProgressText = HasInProgress
            ? $"Ещё {report.InProgressCount} " +
              $"{Formats.Plural(report.InProgressCount, "заказ", "заказа", "заказов")} " +
              $"на {Formats.MoneySmart(report.InProgressRevenue)} ждут выдачи — попадут в отчёт после неё."
            : string.Empty;

        TopItems.Clear();
        var maxQuantity = report.TopItems.Count == 0 ? 1m : report.TopItems.Max(item => item.Quantity);
        foreach (var item in report.TopItems)
        {
            TopItems.Add(new ReportTopRowViewModel(item, (double)(item.Quantity / maxQuantity)));
        }

        Days.Clear();
        var maxRevenue = report.Days.Count == 0 ? 1m : report.Days.Max(day => day.Revenue);
        foreach (var day in report.Days)
        {
            Days.Add(new ReportDayRowViewModel(day, (double)(day.Revenue / maxRevenue)));
        }

        Customers.Clear();
        foreach (var customer in report.Customers)
        {
            Customers.Add(new ReportCustomerRowViewModel(customer));
        }

        CustomerChart.Clear();
        var topCustomers = report.Customers.Take(6).ToList();
        var maxOrders = topCustomers.Count == 0 ? 1 : topCustomers.Max(customer => customer.OrdersCount);
        foreach (var customer in topCustomers)
        {
            CustomerChart.Add(new CustomerChartRowViewModel(customer, (double)customer.OrdersCount / maxOrders));
        }

        CustomersSummary = report.Customers.Count == 0
            ? "За период выданных заказов не было"
            : $"Клиентов: {report.CustomersCount} · новых: {report.NewCustomersCount}";
        IsCustomersEmpty = report.Customers.Count == 0;

        OnPropertyChanged(nameof(IsTopEmpty));
        OnPropertyChanged(nameof(IsDaysEmpty));
    }

    private void NotifyPresets()
    {
        OnPropertyChanged(nameof(WeekActive));
        OnPropertyChanged(nameof(MonthActive));
        OnPropertyChanged(nameof(YearActive));
    }

    public string BuildCsvReport()
    {
        var report = _currentReport;
        if (report is null)
        {
            return string.Empty;
        }

        var text = new StringBuilder();

        void Row(params string[] cells) =>
            text.AppendLine(string.Join(";", cells.Select(Csv)));

        Row("Отчёт за период", Formats.Date(report.From), Formats.Date(report.To));
        Row("Выручка", Num(report.Revenue));
        Row("Себестоимость", Num(report.Cost));
        Row("Прибыль", Num(report.Profit));
        Row("Заказов", report.OrdersCount.ToString());
        Row("Средний чек", Num(report.AverageCheck));
        Row("Закупки", Num(report.PurchasesCost));
        Row("Клиентов", report.CustomersCount.ToString());
        Row("Новых клиентов", report.NewCustomersCount.ToString());
        Row();

        Row("Топ рецептов");
        Row("Позиция", "Количество", "Единица", "Выручка", "Себестоимость", "Прибыль");
        foreach (var item in report.TopItems)
        {
            Row(item.Name, Num(item.Quantity), item.Unit, Num(item.Revenue), Num(item.Cost), Num(item.Profit));
        }

        Row();
        Row("Клиенты");
        Row("№", "Клиент", "Телефон", "Заказов", "Выручка", "Средний чек", "Последний заказ");
        foreach (var customer in report.Customers)
        {
            Row(customer.Number.ToString(), customer.Name, customer.Phone, customer.OrdersCount.ToString(),
                Num(customer.Revenue), Num(customer.AverageCheck), Formats.Date(customer.LastOrderDate));
        }

        Row();
        Row("По дням");
        Row("Дата", "Заказов", "Выручка", "Себестоимость");
        foreach (var day in report.Days)
        {
            Row(Formats.Date(day.Date), day.OrdersCount.ToString(), Num(day.Revenue), Num(day.Cost));
        }

        return text.ToString();
    }

    public string BuildHtmlReport()
    {
        var report = _currentReport;
        if (report is null)
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        text.AppendLine("<!doctype html><html lang=\"ru\"><head><meta charset=\"utf-8\">");
        text.AppendLine("<title>Sweet Taste — отчёт</title><style>");
        text.AppendLine("""
            body { font-family: Arial, Helvetica, sans-serif; margin: 32px; color: #444; }
            h1 { font-family: Arial, Helvetica, sans-serif; font-size: 22px; color: #333;
                 border-bottom: 1px solid #bbb; padding-bottom: 6px; margin: 0 0 6px; }
            h2 { font-family: Arial, Helvetica, sans-serif; font-size: 15px; color: #444;
                 margin: 22px 0 8px; font-weight: 600; }
            .muted { color: #777; }
            table { width: 100%; border-collapse: collapse; margin: 8px 0 24px;
                    font-size: 12.5px; border: 1px solid #ccc; }
            th { text-align: left; background: #efefef; color: #444; border: 1px solid #ccc;
                 padding: 6px 8px; font-size: 11px; font-weight: 600; }
            td { border: 1px solid #ddd; padding: 6px 8px; color: #444; }
            tr:nth-child(even) td { background: #fafafa; }
            td.r, th.r { text-align: right; }
            @media print { body { margin: 12mm; } tr { break-inside: avoid; } }
            """);
        text.AppendLine("</style></head><body>");
        text.AppendLine("<h1>Sweet Taste — отчёт</h1>");
        text.AppendLine($"<p class=\"muted\">По дате выдачи: {Formats.Date(report.From)} — {Formats.Date(report.To)}</p>");

        text.AppendLine("<h2>Итоги</h2><table>");
        text.AppendLine("<tr><th>Выручка</th><th>Прибыль</th><th>Заказов</th></tr>");
        text.AppendLine($"<tr><td>{H(Formats.MoneySmart(report.Revenue))}</td>" +
                        $"<td>{H(Formats.MoneySmart(report.Profit))}</td>" +
                        $"<td>{report.OrdersCount}</td></tr>");
        text.AppendLine("<tr><th>Средний чек</th><th>Закупки</th><th>Клиентов</th></tr>");
        text.AppendLine($"<tr><td>{H(Formats.MoneySmart(report.AverageCheck))}</td>" +
                        $"<td>{H(Formats.MoneySmart(report.PurchasesCost))}</td>" +
                        $"<td>{report.CustomersCount} (новых: {report.NewCustomersCount})</td></tr>");
        text.AppendLine("</table>");

        text.AppendLine("<h2>Топ рецептов</h2><table><tr><th>Позиция</th><th class=\"r\">Количество</th>");
        text.AppendLine("<th class=\"r\">Выручка</th><th class=\"r\">Прибыль</th></tr>");
        foreach (var item in report.TopItems)
        {
            text.AppendLine(
                $"<tr><td>{H(item.Name)}</td><td class=\"r\">{H(Formats.Quantity(item.Quantity, item.Unit))}</td>" +
                $"<td class=\"r\">{H(Formats.MoneySmart(item.Revenue))}</td>" +
                $"<td class=\"r\">{H(Formats.MoneySmart(item.Profit))}</td></tr>");
        }

        text.AppendLine("</table>");

        text.AppendLine("<h2>Клиенты</h2><table><tr><th>№</th><th>Клиент</th><th>Телефон</th>");
        text.AppendLine("<th class=\"r\">Заказов</th><th class=\"r\">Выручка</th><th class=\"r\">Средний чек</th>");
        text.AppendLine("<th class=\"r\">Последний</th></tr>");
        foreach (var customer in report.Customers)
        {
            text.AppendLine(
                $"<tr><td>{customer.Number}</td><td>{H(customer.Name)}</td><td>{H(customer.Phone)}</td>" +
                $"<td class=\"r\">{customer.OrdersCount}</td><td class=\"r\">{H(Formats.MoneySmart(customer.Revenue))}</td>" +
                $"<td class=\"r\">{H(Formats.MoneySmart(customer.AverageCheck))}</td>" +
                $"<td class=\"r\">{H(Formats.Date(customer.LastOrderDate))}</td></tr>");
        }

        text.AppendLine("</table>");

        text.AppendLine("<h2>По дням</h2><table><tr><th>Дата</th><th class=\"r\">Заказов</th>");
        text.AppendLine("<th class=\"r\">Выручка</th><th class=\"r\">Себестоимость</th></tr>");
        foreach (var day in report.Days)
        {
            text.AppendLine(
                $"<tr><td>{H(Formats.Date(day.Date))}</td><td class=\"r\">{day.OrdersCount}</td>" +
                $"<td class=\"r\">{H(Formats.MoneySmart(day.Revenue))}</td>" +
                $"<td class=\"r\">{H(Formats.MoneySmart(day.Cost))}</td></tr>");
        }

        text.AppendLine("</table></body></html>");
        return text.ToString();
    }

    private static string H(string? value) => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);

    private static string Csv(string? value)
    {
        var text = value ?? string.Empty;
        return text.Contains(';') || text.Contains('"') || text.Contains('\n')
            ? "\"" + text.Replace("\"", "\"\"") + "\""
            : text;
    }

    private static string Num(decimal value) =>
        value.ToString("0.00", CultureInfo.GetCultureInfo("ru-RU"));

    private static DateTimeOffset ToDateTimeOffset(DateOnly date) =>
        new(date.ToDateTime(TimeOnly.MinValue));

    internal static IBrush ThemeBrush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush
            ? brush
            : Brushes.Gray;
}

/// <summary>Строка топа рецептов.</summary>
public sealed class ReportTopRowViewModel : ViewModelBase
{
    public ReportTopRowViewModel(ReportTopItem item, double barValue)
    {
        Name = item.Name;
        QuantityText = Formats.Quantity(item.Quantity, item.Unit);
        RevenueText = Formats.MoneySmart(item.Revenue);
        ProfitText = Formats.MoneySmart(item.Profit);
        ProfitBrush = ReportsViewModel.ThemeBrush(item.Profit < 0m ? "DangerBrush" : "SuccessBrush");
        BarValue = barValue;
    }

    public string Name { get; }

    public string QuantityText { get; }

    public string RevenueText { get; }

    public string ProfitText { get; }

    public IBrush ProfitBrush { get; }

    public double BarValue { get; }
}

/// <summary>Строка разбивки по дням.</summary>
public sealed class ReportDayRowViewModel : ViewModelBase
{
    public ReportDayRowViewModel(ReportDay day, double barValue)
    {
        DateText = day.Date.ToString("dd.MM");
        OrdersText = day.OrdersCount.ToString();
        RevenueText = Formats.MoneySmart(day.Revenue);
        BarValue = barValue;
    }

    public string DateText { get; }

    public string OrdersText { get; }

    public string RevenueText { get; }

    public double BarValue { get; }
}

/// <summary>Клиент в отчёте за период.</summary>
public sealed class ReportCustomerRowViewModel : ViewModelBase
{
    public ReportCustomerRowViewModel(ReportCustomer customer)
    {
        NumberText = customer.Number > 0 ? "№ " + customer.Number : "—";
        Name = customer.Name;
        Phone = customer.Phone;
        OrdersText = customer.OrdersCount.ToString();
        RevenueText = Formats.MoneySmart(customer.Revenue);
        AverageCheckText = Formats.MoneySmart(customer.AverageCheck);
        LastOrderText = Formats.Date(customer.LastOrderDate);
    }

    public string NumberText { get; }

    public string Name { get; }

    public string Phone { get; }

    public string OrdersText { get; }

    public string RevenueText { get; }

    public string AverageCheckText { get; }

    public string LastOrderText { get; }
}

/// <summary>Столбик диаграммы «кто заказывает чаще».</summary>
public sealed class CustomerChartRowViewModel : ViewModelBase
{
    public CustomerChartRowViewModel(ReportCustomer customer, double barValue)
    {
        Name = customer.Name;
        OrdersText = customer.OrdersCount.ToString();
        BarValue = barValue;
    }

    public string Name { get; }

    public string OrdersText { get; }

    public double BarValue { get; }
}
