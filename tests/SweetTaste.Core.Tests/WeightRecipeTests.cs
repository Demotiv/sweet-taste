using SweetTaste.Core.Domain;
using SweetTaste.Core.Services;

namespace SweetTaste.Core.Tests;

public class WeightRecipeTests
{
    private static (TestDatabase Db, RecipeService Recipes, OrderService Orders,
        Guid Cake, Guid Pastry, Guid Cupcake) CreateBase()
    {
        var db = new TestDatabase();
        var flour = db.Service.AddIngredient("Мука", null, Units.Grams);
        var sugar = db.Service.AddIngredient("Сахар", null, Units.Grams);
        var butter = db.Service.AddIngredient("Масло", null, Units.Grams);

        var today = DateOnly.FromDateTime(DateTime.Now);
        db.Service.AddPurchase(flour.Id, today, "Магнит", 1000m, 100m);   // 0.10 ₽/г
        db.Service.AddPurchase(sugar.Id, today, "Магнит", 1000m, 200m);   // 0.20 ₽/г
        db.Service.AddPurchase(butter.Id, today, "Магнит", 1000m, 1000m); // 1.00 ₽/г

        var recipes = new RecipeService(db.Database, db.Service);

        // Партия 2 кг: 50 + 60 + 400 = 510 ₽ → 255 ₽/кг, наценка 100% → 510 ₽/кг.
        var cake = recipes.CreateRecipe("Торт", null, 2m, RecipeUnits.Kilograms, 100,
            new[] { (flour.Id, 500m), (sugar.Id, 300m), (butter.Id, 400m) });

        // Партия 350 г: 25 + 150 = 175 ₽ → 0.50 ₽/г, наценка 100% → 1 ₽/г.
        var pastry = recipes.CreateRecipe("Пирожное", null, 350m, RecipeUnits.Grams, 100,
            new[] { (flour.Id, 250m), (butter.Id, 150m) });

        var cupcake = recipes.CreateRecipe("Капкейк", null, 6m, RecipeUnits.Pieces, 100,
            new[] { (flour.Id, 120m), (sugar.Id, 60m) });

        var orders = new OrderService(db.Database, recipes);
        return (db, recipes, orders, cake.Id, pastry.Id, cupcake.Id);
    }

    private static DateOnly Tomorrow => DateOnly.FromDateTime(DateTime.Now).AddDays(1);

    [Fact]
    public void WeightRecipe_ScalesIngredientsForOrder()
    {
        var (db, recipes, orders, cake, pastry, cupcake) = CreateBase();
        using var _ = db;

        var order = orders.CreateOrder("Иван", null, "1", Tomorrow, null, new[] { (cake, 1.5m) });
        var details = orders.GetOrderDetails(order.Id);

        Assert.Equal(765m, order.TotalPrice); // 510 ₽/кг × 1,5 кг
        Assert.Equal(382.5m, order.TotalCost);
        Assert.Equal(382.5m, details.ShoppingCost);

        var flour = details.ShoppingList.Single(item => item.IngredientName == "Мука");
        var sugar = details.ShoppingList.Single(item => item.IngredientName == "Сахар");
        var butter = details.ShoppingList.Single(item => item.IngredientName == "Масло");

        Assert.Equal(375m, flour.Amount);  // 500 г × 0,75
        Assert.Equal(225m, sugar.Amount);  // 300 г × 0,75
        Assert.Equal(300m, butter.Amount); // 400 г × 0,75
        Assert.Equal("кг", order.Items.Single().OutputUnit);
        Assert.Equal(1.5m, order.Items.Single().Quantity);
    }

    [Fact]
    public void GramRecipe_PricesPerGramAndScales()
    {
        var (db, recipes, orders, cake, pastry, cupcake) = CreateBase();
        using var _ = db;

        var summary = recipes.GetRecipes().Single(item => item.Recipe.Id == pastry);
        Assert.Equal(0.5m, summary.CostPerUnit);
        Assert.Equal(1m, summary.PricePerUnit);

        var order = orders.CreateOrder("Иван", null, "1", Tomorrow, null, new[] { (pastry, 500m) });

        Assert.Equal(500m, order.TotalPrice);
        Assert.Equal(250m, order.TotalCost);
        Assert.Equal("г", order.Items.Single().OutputUnit);

        var details = orders.GetOrderDetails(order.Id);
        Assert.Equal(357.143m, details.ShoppingList.Single(item => item.IngredientName == "Мука").Amount, 3);
        Assert.Equal(214.286m, details.ShoppingList.Single(item => item.IngredientName == "Масло").Amount, 3);
    }

    [Fact]
    public void PieceRecipe_RequiresWholeQuantity()
    {
        var (db, recipes, orders, cake, pastry, cupcake) = CreateBase();
        using var _ = db;

        Assert.Throws<ValidationException>(() =>
            orders.CreateOrder("Иван", null, "1", Tomorrow, null, new[] { (cupcake, 1.5m) }));

        var order = orders.CreateOrder("Иван", null, "1", Tomorrow, null, new[] { (cupcake, 6m) });
        Assert.Equal(6m, order.Items.Single().Quantity);
        Assert.Equal("шт", order.Items.Single().OutputUnit);
    }

    [Fact]
    public void CreateRecipe_ValidatesUnitAndFraction()
    {
        var (db, recipes, orders, cake, pastry, cupcake) = CreateBase();
        using var _ = db;

        var flour = db.Service.GetIngredients().Single(item => item.Ingredient.Name == "Мука").Ingredient.Id;

        Assert.Throws<ValidationException>(() =>
            recipes.CreateRecipe("Полштуки", null, 1.5m, RecipeUnits.Pieces, 100, new[] { (flour, 100m) }));

        Assert.Throws<ValidationException>(() =>
            recipes.CreateRecipe("Порции", null, 2m, "порция", 100, new[] { (flour, 100m) }));

        Assert.Throws<ValidationException>(() =>
            recipes.CreateRecipe("Ноль", null, 0m, RecipeUnits.Kilograms, 100, new[] { (flour, 100m) }));
    }

    [Fact]
    public void Recipe_KeepsFractionalOutput()
    {
        var (db, recipes, orders, cake, pastry, cupcake) = CreateBase();
        using var _ = db;

        var created = recipes.CreateRecipe("Торт 2,5 кг", null, 2.5m, RecipeUnits.Kilograms, 80,
            new[] { (db.Service.GetIngredients().Single(item => item.Ingredient.Name == "Мука").Ingredient.Id, 500m) });

        var loaded = recipes.GetRecipes().Single(item => item.Recipe.Id == created.Id);
        Assert.Equal(2.5m, loaded.Recipe.OutputQuantity);
        Assert.Equal(RecipeUnits.Kilograms, loaded.Recipe.OutputUnit);
    }

    [Fact]
    public void Order_KeepsDuplicateRecipeLinesSeparate()
    {
        var (db, recipes, orders, cake, pastry, cupcake) = CreateBase();
        using var _ = db;

        var order = orders.CreateOrder("Иван", null, "1", Tomorrow, null,
            new[] { (cake, 0.5m), (cake, 0.7m) });

        Assert.Equal(2, order.Items.Count);
        Assert.Equal(0.5m, order.Items[0].Quantity);
        Assert.Equal(0.7m, order.Items[1].Quantity);
        Assert.Equal(612m, order.TotalPrice); // 510 ₽/кг × 1,2 кг
    }

    [Fact]
    public void Order_CountsIngredientsForEachPositionSeparately()
    {
        var (db, recipes, orders, cake, pastry, cupcake) = CreateBase();
        using var _ = db;

        var order = orders.CreateOrder("Иван", null, "1", Tomorrow, null,
            new[] { (cake, 3.5m), (cake, 4.9m) });
        var details = orders.GetOrderDetails(order.Id);

        Assert.Equal(2, order.Items.Count);
        Assert.Equal(2, details.ItemShopping.Count);

        var first = details.ItemShopping[0];
        Assert.Equal(3.5m, first.Item.Quantity);
        Assert.Equal(892.5m, first.Cost);
        Assert.Equal(875m, first.Lines.Single(line => line.IngredientName == "Мука").Amount);

        var second = details.ItemShopping[1];
        Assert.Equal(4.9m, second.Item.Quantity);
        Assert.Equal(1249.5m, second.Cost);
        Assert.Equal(1225m, second.Lines.Single(line => line.IngredientName == "Мука").Amount);

        var totalFlour = details.ShoppingList.Single(line => line.IngredientName == "Мука");
        Assert.Equal(2100m, totalFlour.Amount);
        Assert.Equal(2142m, details.ShoppingCost);
    }

    [Fact]
    public void Report_TopItemsKeepUnit()
    {
        var (db, recipes, orders, cake, pastry, cupcake) = CreateBase();
        using var _ = db;

        var order = orders.CreateOrder("Иван", null, "1", Tomorrow, null, new[] { (cake, 1.5m) });
        orders.AdvanceOrder(order.Id);
        orders.AdvanceOrder(order.Id);
        orders.AdvanceOrder(order.Id);

        var today = DateOnly.FromDateTime(DateTime.Now);
        var report = new ReportService(orders, db.Service).Build(today, today.AddDays(7));

        var top = Assert.Single(report.TopItems);
        Assert.Equal("Торт", top.Name);
        Assert.Equal("кг", top.Unit);
        Assert.Equal(1.5m, top.Quantity);
        Assert.Equal(765m, top.Revenue);
    }
}
