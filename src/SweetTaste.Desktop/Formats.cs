using System.Globalization;
using SweetTaste.Core.Domain;

namespace SweetTaste.Desktop;

/// <summary>Форматирование для интерфейса: рубли, даты, количества, склонения.</summary>
public static class Formats
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static string Money(decimal value) => value.ToString("N2", Ru) + " ₽";

    public static string Money0(decimal value) => value.ToString("N0", Ru) + " ₽";

    /// <summary>Без копеек, если сумма целая — иначе точная.</summary>
    public static string MoneySmart(decimal value) =>
        value == decimal.Truncate(value) ? Money0(value) : Money(value);

    public static string Price(decimal value) => (value < 0.01m ? value.ToString("0.###", Ru) : value.ToString("0.00", Ru)) + " ₽";

    public static string PerUnit(decimal value, string unit) => Price(value) + " / " + unit;

    public static string UnitPrice(decimal value, string unit) =>
        (string.Equals(unit, RecipeUnits.Grams, StringComparison.Ordinal) ? Money(value) : MoneySmart(value)) + " / " + unit;

    public static string Quantity(decimal value, string unit) => Units.FormatQuantity(value, unit);

    public static string Output(decimal quantity, string unit) => Units.FormatQuantity(quantity, unit);

    public static string Date(DateOnly value) => value.ToString("dd.MM.yyyy", Ru);

    public static string Moment(DateTimeOffset value) => value.ToString("dd.MM.yyyy HH:mm", Ru);

    public static string ShortDate(DateOnly value) => value.ToString("dd.MM", Ru);

    public static string RelativeDate(DateOnly value)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (value == today)
        {
            return "сегодня";
        }

        if (value == today.AddDays(-1))
        {
            return "вчера";
        }

        return value.ToString("dd.MM.yyyy", Ru);
    }

    public static string Purchases(int count) => count + " " + Plural(count, "закупка", "закупки", "закупок");

    public static string Ingredients(int count) => count + " " + Plural(count, "ингредиент", "ингредиента", "ингредиентов");

    public static string OrderStatusText(OrderStatus status) => status switch
    {
        OrderStatus.New => "Новый",
        OrderStatus.Cooking => "Готовится",
        OrderStatus.Ready => "Готов",
        OrderStatus.Done => "Выдан",
        OrderStatus.Cancelled => "Отменён",
        _ => status.ToString(),
    };

    public static string Plural(int count, string one, string few, string many)
    {
        var mod100 = Math.Abs(count) % 100;
        if (mod100 is >= 11 and <= 14)
        {
            return many;
        }

        return (mod100 % 10) switch
        {
            1 => one,
            >= 2 and <= 4 => few,
            _ => many,
        };
    }
}
