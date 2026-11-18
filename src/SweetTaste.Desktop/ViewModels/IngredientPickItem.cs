using SweetTaste.Core.Domain;

namespace SweetTaste.Desktop.ViewModels;

/// <summary>
/// Элемент выпадающего списка ингредиентов. Заголовок категории не выбирается,
/// под ним идут сами ингредиенты.
/// </summary>
public sealed class IngredientPickItem
{
    private IngredientPickItem(Guid id, string name, string unit, string? categoryName, bool isHeader)
    {
        Id = id;
        Name = name;
        Unit = unit;
        CategoryName = categoryName;
        IsHeader = isHeader;
    }

    public Guid Id { get; }

    public string Name { get; }

    public string Unit { get; }

    public string? CategoryName { get; }

    public bool IsHeader { get; }

    public static IngredientPickItem Header(string categoryName) =>
        new(Guid.Empty, categoryName, string.Empty, categoryName, isHeader: true);

    public static IngredientPickItem FromPrice(IngredientPrice price) =>
        new(price.Id, price.Name, price.Unit, price.CategoryName, isHeader: false);

    public override string ToString() => Name;
}

/// <summary>Собирает плоский список с заголовками категорий для ComboBox.</summary>
public static class IngredientPickList
{
    public const string NoCategory = "Без категории";

    public static List<IngredientPickItem> Build(IEnumerable<IngredientPrice> ingredients)
    {
        var result = new List<IngredientPickItem>();

        var groups = ingredients
            .GroupBy(ingredient => ingredient.CategoryName ?? NoCategory)
            .OrderBy(group => group.Key == NoCategory ? 1 : 0)
            .ThenBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase);

        foreach (var group in groups)
        {
            result.Add(IngredientPickItem.Header(group.Key));

            foreach (var ingredient in group.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                result.Add(IngredientPickItem.FromPrice(ingredient));
            }
        }

        return result;
    }
}
