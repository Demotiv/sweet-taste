using System.Globalization;
using Microsoft.Data.Sqlite;
using SweetTaste.Core.Domain;
using SweetTaste.Core.Storage;

namespace SweetTaste.Core.Services;

/// <summary>Заказы: клиент, состав из рецептов, срок, статусы и список покупок.</summary>
public sealed class OrderService
{
    private const int MaxNameLength = 60;
    private const int MaxPhoneLength = 30;
    private const int MaxCommentLength = 300;
    private const decimal MaxPieceQuantity = 1_000m;
    private const decimal MaxWeightQuantity = 1_000_000m;
    private const int MaxPackagingQuantity = 1_000;

    private readonly AppDatabase _database;
    private readonly RecipeService _recipes;
    private readonly PackagingService _packaging;
    private readonly CustomerService _customers;

    public OrderService(AppDatabase database, RecipeService recipes)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _recipes = recipes ?? throw new ArgumentNullException(nameof(recipes));
        _packaging = new PackagingService(database);
        _customers = new CustomerService(database);
        _database.Initialize();
    }

    public IReadOnlyList<Order> GetOrders()
    {
        using var connection = _database.Open();
        var items = LoadItems(connection);
        var packaging = LoadPackaging(connection);

        return LoadOrders(connection)
            .Select(order => order with
            {
                Items = items.TryGetValue(order.Id, out var list) ? list : new List<OrderItem>(),
                Packaging = packaging.TryGetValue(order.Id, out var boxes)
                    ? boxes
                    : new List<OrderPackaging>(),
            })
            .OrderByDescending(order => order.Number)
            .ToList();
    }

    public OrderDetails GetOrderDetails(Guid orderId)
    {
        var order = GetOrders().FirstOrDefault(item => item.Id == orderId)
            ?? throw new ValidationException("Заказ не найден.");

        var shopping = new Dictionary<(string Name, string Unit), (decimal Amount, decimal Cost)>();
        var itemShopping = new List<OrderItemShopping>();

        foreach (var item in order.Items)
        {
            var lines = new List<ShoppingListItem>();
            var itemCost = 0m;
            var recipe = TryGetRecipeDetails(item.RecipeId);

            if (recipe is not null && recipe.Recipe.OutputQuantity > 0m)
            {
                var scale = item.Quantity / recipe.Recipe.OutputQuantity;

                foreach (var line in recipe.Lines)
                {
                    var amount = line.Amount * scale;
                    var cost = (line.AveragePricePerUnit ?? 0m) * amount;

                    lines.Add(new ShoppingListItem(line.Ingredient.Name, line.Ingredient.Unit, amount, cost));
                    itemCost += cost;

                    var key = (line.Ingredient.Name, line.Ingredient.Unit);
                    if (shopping.TryGetValue(key, out var existing))
                    {
                        shopping[key] = (existing.Amount + amount, existing.Cost + cost);
                    }
                    else
                    {
                        shopping[key] = (amount, cost);
                    }
                }
            }

            lines.Sort((left, right) =>
                StringComparer.CurrentCultureIgnoreCase.Compare(left.IngredientName, right.IngredientName));

            itemShopping.Add(new OrderItemShopping(item, lines, itemCost));
        }

        var list = shopping
            .Select(pair => new ShoppingListItem(pair.Key.Name, pair.Key.Unit, pair.Value.Amount, pair.Value.Cost))
            .OrderBy(item => item.IngredientName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new OrderDetails(order, itemShopping, list, list.Sum(item => item.EstimatedCost));
    }

    private RecipeDetails? TryGetRecipeDetails(Guid? recipeId)
    {
        if (recipeId is not { } id)
        {
            return null;
        }

        try
        {
            return _recipes.GetRecipeDetails(id);
        }
        catch (ValidationException)
        {
            return null;
        }
    }

    public Order CreateOrder(
        string firstName,
        string? lastName,
        string phone,
        DateOnly dueDate,
        string? comment,
        IEnumerable<(Guid RecipeId, decimal Quantity)> lines,
        decimal paidAmount = 0m,
        decimal discountAmount = 0m)
    {
        var client = ValidateClient(firstName, lastName, phone);
        ValidateDueDate(dueDate);
        var items = BuildItems(lines);
        var customer = _customers.ResolveOrCreate(client.FirstName, client.LastName, client.Phone);
        var now = DateTimeOffset.Now;
        var baseTotal = CalculateTotal(items);
        var discount = ValidateDiscount(discountAmount, baseTotal);
        var total = RoundUpTotal(baseTotal - discount);
        var paid = ValidatePaid(paidAmount, total);

        using var connection = _database.Open();
        var number = GetNextNumber(connection);
        var order = new Order(
            Guid.NewGuid(),
            number,
            client.FirstName,
            client.LastName,
            client.Phone,
            customer.Id,
            now,
            dueDate,
            OrderStatus.New,
            CleanComment(comment),
            items,
            Array.Empty<OrderPackaging>(),
            total,
            discount,
            paid,
            now);

        using var transaction = connection.BeginTransaction();
        InsertOrder(connection, transaction, order);
        InsertItems(connection, transaction, order);
        transaction.Commit();

        return order;
    }

    public Order UpdateOrder(
        Guid orderId,
        string firstName,
        string? lastName,
        string phone,
        DateOnly dueDate,
        string? comment,
        IEnumerable<(Guid RecipeId, decimal Quantity)> lines,
        decimal? paidAmount = null,
        decimal? discountAmount = null)
    {
        ValidateDueDate(dueDate);
        var client = ValidateClient(firstName, lastName, phone);
        var items = BuildItems(lines);

        using var connection = _database.Open();
        var existing = FindOrder(connection, orderId)
            ?? throw new ValidationException("Заказ не найден.");

        if (OrderStatuses.IsFinal(existing.Status))
        {
            throw new ValidationException("Завершённый заказ нельзя редактировать.");
        }

        var packaging = LoadPackaging(connection).TryGetValue(orderId, out var existingPackaging)
            ? existingPackaging
            : new List<OrderPackaging>();
        var factor = MarkupFactor(items);
        var revaluedPackaging = packaging
            .Select(box => box with { SalePrice = RoundMoney(box.CostPrice * factor) })
            .ToList();
        var customer = _customers.ResolveOrCreate(client.FirstName, client.LastName, client.Phone);
        var baseTotal = CalculateTotal(items, revaluedPackaging);
        var discount = discountAmount.HasValue
            ? ValidateDiscount(discountAmount.Value, baseTotal)
            : Math.Min(existing.DiscountAmount, baseTotal);
        var total = RoundUpTotal(baseTotal - discount);
        var paid = paidAmount.HasValue
            ? ValidatePaid(paidAmount.Value, total)
            : Math.Min(existing.PaidAmount, total);

        var updated = existing with
        {
            FirstName = client.FirstName,
            LastName = client.LastName,
            Phone = client.Phone,
            CustomerId = customer.Id,
            DueDate = dueDate,
            Comment = CleanComment(comment),
            Items = items,
            Packaging = revaluedPackaging,
            TotalPrice = total,
            DiscountAmount = discount,
            PaidAmount = paid,
            UpdatedAt = DateTimeOffset.Now,
        };

        using var transaction = connection.BeginTransaction();

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE orders
                SET first_name = $first,
                    last_name = $last,
                    phone = $phone,
                    customer_id = $customer,
                    due_date = $due,
                    comment = $comment,
                    total_price_kopecks = $total,
                    discount_kopecks = $discount,
                    paid_amount_kopecks = $paid,
                    updated_at = $updated
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", updated.Id.ToString());
            command.Parameters.AddWithValue("$first", updated.FirstName);
            command.Parameters.AddWithValue("$last", (object?)updated.LastName ?? DBNull.Value);
            command.Parameters.AddWithValue("$phone", updated.Phone);
            command.Parameters.AddWithValue("$customer", (object?)updated.CustomerId?.ToString() ?? DBNull.Value);
            command.Parameters.AddWithValue("$due", updated.DueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$comment", (object?)updated.Comment ?? DBNull.Value);
            command.Parameters.AddWithValue("$total", ToKopecks(updated.TotalPrice));
            command.Parameters.AddWithValue("$discount", ToKopecks(updated.DiscountAmount));
            command.Parameters.AddWithValue("$paid", ToKopecks(updated.PaidAmount));
            command.Parameters.AddWithValue("$updated", updated.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM order_items WHERE order_id = $id;";
            delete.Parameters.AddWithValue("$id", updated.Id.ToString());
            delete.ExecuteNonQuery();
        }

        InsertItems(connection, transaction, updated);

        foreach (var box in updated.Packaging)
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE order_packaging SET sale_price_kopecks = $price WHERE id = $id;";
            update.Parameters.AddWithValue("$id", box.Id.ToString());
            update.Parameters.AddWithValue("$price", ToKopecks(box.SalePrice));
            update.ExecuteNonQuery();
        }

        transaction.Commit();

        return updated;
    }

    /// <summary>
    /// Выбирает упаковку для заказа и пересчитывает итог.
    /// Цена коробки — закупочная; к ней применяется средневзвешенная наценка рецептов заказа.
    /// </summary>
    public Order SetOrderPackaging(Guid orderId, IEnumerable<(Guid PackagingId, int Quantity)> lines)
    {
        var order = GetOrders().FirstOrDefault(item => item.Id == orderId)
            ?? throw new ValidationException("Заказ не найден.");

        if (OrderStatuses.IsFinal(order.Status))
        {
            throw new ValidationException("Завершённый заказ нельзя менять.");
        }

        var merged = new Dictionary<Guid, int>();
        foreach (var line in lines)
        {
            if (line.Quantity <= 0)
            {
                continue;
            }

            if (line.Quantity > MaxPackagingQuantity)
            {
                throw new ValidationException($"Коробок в одной строке не больше {MaxPackagingQuantity}.");
            }

            merged[line.PackagingId] = merged.GetValueOrDefault(line.PackagingId) + line.Quantity;
        }

        var catalog = _packaging.GetItems().ToDictionary(summary => summary.Item.Id, summary => summary.Item);
        var factor = MarkupFactor(order.Items);
        var rows = new List<OrderPackaging>();

        foreach (var pair in merged)
        {
            if (!catalog.TryGetValue(pair.Key, out var item))
            {
                throw new ValidationException("Одна из коробок больше недоступна. Обновите список.");
            }

            rows.Add(new OrderPackaging(
                Guid.NewGuid(),
                item.Id,
                item.Name,
                item.Size,
                item.Price,
                RoundMoney(item.Price * factor),
                pair.Value));
        }

        var baseTotal = CalculateTotal(order.Items, rows);
        var discount = Math.Min(order.DiscountAmount, baseTotal);
        var total = RoundUpTotal(baseTotal - discount);
        var paid = Math.Min(order.PaidAmount, total);
        var now = DateTimeOffset.Now;

        using (var connection = _database.Open())
        using (var transaction = connection.BeginTransaction())
        {
            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM order_packaging WHERE order_id = $id;";
                delete.Parameters.AddWithValue("$id", orderId.ToString());
                delete.ExecuteNonQuery();
            }

            for (var index = 0; index < rows.Count; index++)
            {
                InsertPackaging(connection, transaction, orderId, rows[index], index);
            }

            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE orders
                SET total_price_kopecks = $total,
                    discount_kopecks = $discount,
                    paid_amount_kopecks = $paid,
                    updated_at = $updated
                WHERE id = $id;
                """;
            update.Parameters.AddWithValue("$id", orderId.ToString());
            update.Parameters.AddWithValue("$total", ToKopecks(total));
            update.Parameters.AddWithValue("$discount", ToKopecks(discount));
            update.Parameters.AddWithValue("$paid", ToKopecks(paid));
            update.Parameters.AddWithValue("$updated", now.ToString("O", CultureInfo.InvariantCulture));
            update.ExecuteNonQuery();

            transaction.Commit();
        }

        return order with
        {
            Packaging = rows,
            TotalPrice = total,
            DiscountAmount = discount,
            PaidAmount = paid,
            UpdatedAt = now,
        };
    }

    /// <summary>Меняет сумму оплаты. Больше суммы заказа заплатить нельзя.</summary>
    public Order SetOrderPaid(Guid orderId, decimal amount)
    {
        var order = GetOrders().FirstOrDefault(item => item.Id == orderId)
            ?? throw new ValidationException("Заказ не найден.");

        if (order.Status == OrderStatus.Cancelled)
        {
            throw new ValidationException("Отменённый заказ нельзя оплатить.");
        }

        var paid = ValidatePaid(amount, order.TotalPrice);
        var now = DateTimeOffset.Now;

        using (var connection = _database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE orders
                SET paid_amount_kopecks = $paid, updated_at = $updated
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", orderId.ToString());
            command.Parameters.AddWithValue("$paid", ToKopecks(paid));
            command.Parameters.AddWithValue("$updated", now.ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        return order with { PaidAmount = paid, UpdatedAt = now };
    }

    /// <summary>Отмечает заказ полностью оплаченным.</summary>
    public Order MarkOrderPaid(Guid orderId)
    {
        var order = GetOrders().FirstOrDefault(item => item.Id == orderId)
            ?? throw new ValidationException("Заказ не найден.");

        return SetOrderPaid(orderId, order.TotalPrice);
    }

    private static decimal ValidateDiscount(decimal amount, decimal baseTotal)
    {
        var discount = decimal.Round(amount, 2, MidpointRounding.AwayFromZero);

        if (discount < 0m)
        {
            throw new ValidationException("Скидка не может быть отрицательной.");
        }

        if (discount > baseTotal)
        {
            throw new ValidationException("Скидка не может быть больше суммы заказа.");
        }

        return discount;
    }

    private static decimal ValidatePaid(decimal amount, decimal total)
    {
        var paid = decimal.Round(amount, 2, MidpointRounding.AwayFromZero);

        if (paid < 0m)
        {
            throw new ValidationException("Оплата не может быть отрицательной.");
        }

        if (paid > total)
        {
            throw new ValidationException("Оплата не может быть больше суммы заказа.");
        }

        return paid;
    }

    public OrderStatus AdvanceOrder(Guid orderId)
    {
        using var connection = _database.Open();
        var order = FindOrder(connection, orderId)
            ?? throw new ValidationException("Заказ не найден.");

        if (OrderStatuses.IsFinal(order.Status))
        {
            throw new ValidationException("Заказ уже закрыт.");
        }

        var status = OrderStatuses.Advance(order.Status);
        UpdateStatus(connection, orderId, status);
        return status;
    }

    public void CancelOrder(Guid orderId)
    {
        using var connection = _database.Open();
        var order = FindOrder(connection, orderId)
            ?? throw new ValidationException("Заказ не найден.");

        if (OrderStatuses.IsFinal(order.Status))
        {
            throw new ValidationException("Завершённый заказ нельзя отменить.");
        }

        UpdateStatus(connection, orderId, OrderStatus.Cancelled);
    }

    /// <summary>
    /// Переводит заказ в любой статус, включая возврат из архива.
    /// Нужно, чтобы исправить случайное нажатие «Выдан» или «Отменён».
    /// </summary>
    public OrderStatus SetOrderStatus(Guid orderId, OrderStatus status)
    {
        if (!Enum.IsDefined(typeof(OrderStatus), status))
        {
            throw new ValidationException("Неизвестный статус заказа.");
        }

        using var connection = _database.Open();
        var order = FindOrder(connection, orderId)
            ?? throw new ValidationException("Заказ не найден.");

        if (order.Status == status)
        {
            return status;
        }

        UpdateStatus(connection, orderId, status);
        return status;
    }

    /// <summary>Удаляет выданные и отменённые заказы — источник выручки в отчётах.</summary>
    public int DeleteArchivedOrders()
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        using (var clearLinks = connection.CreateCommand())
        {
            clearLinks.Transaction = transaction;
            clearLinks.CommandText = """
                UPDATE purchases
                SET order_id = NULL
                WHERE order_id IN (SELECT id FROM orders WHERE status IN ('Done', 'Cancelled'));
                """;
            clearLinks.ExecuteNonQuery();
        }

        using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM orders WHERE status IN ('Done', 'Cancelled');";
        var deleted = delete.ExecuteNonQuery();
        transaction.Commit();
        return deleted;
    }

    /// <summary>Удаляет все заказы, включая текущие. Ингредиенты, рецепты и закупки остаются.</summary>
    public int DeleteAllOrders()
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        using (var clearLinks = connection.CreateCommand())
        {
            clearLinks.Transaction = transaction;
            clearLinks.CommandText = "UPDATE purchases SET order_id = NULL WHERE order_id IS NOT NULL;";
            clearLinks.ExecuteNonQuery();
        }

        using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM orders;";
        var deleted = delete.ExecuteNonQuery();
        transaction.Commit();
        return deleted;
    }

    private static void UpdateStatus(SqliteConnection connection, Guid orderId, OrderStatus status)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE orders
            SET status = $status, updated_at = $updated
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", orderId.ToString());
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$updated", DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    private List<OrderItem> BuildItems(IEnumerable<(Guid RecipeId, decimal Quantity)> lines)
    {
        // Одинаковые рецепты не склеиваем: каждый торт остаётся отдельной позицией,
        // чтобы ингредиенты считались и показывались по каждому заказу отдельно.
        var requested = new List<(Guid RecipeId, decimal Quantity)>();

        foreach (var line in lines)
        {
            var quantity = decimal.Round(line.Quantity, 3, MidpointRounding.AwayFromZero);
            if (quantity <= 0m)
            {
                continue;
            }

            requested.Add((line.RecipeId, quantity));
        }

        if (requested.Count == 0)
        {
            throw new ValidationException("Добавьте в заказ хотя бы одно блюдо.");
        }

        var items = new List<OrderItem>();
        foreach (var line in requested)
        {
            RecipeDetails details;
            try
            {
                details = _recipes.GetRecipeDetails(line.RecipeId);
            }
            catch (ValidationException)
            {
                throw new ValidationException("Одно из блюд недоступно (возможно, оно в корзине). Обновите заказ.");
            }

            ValidateQuantity(line.Quantity, details.Recipe.OutputUnit);

            items.Add(new OrderItem(
                Guid.NewGuid(),
                details.Recipe.Id,
                details.Recipe.Name,
                line.Quantity,
                details.PricePerUnit,
                details.CostPerUnit,
                details.Recipe.OutputQuantity,
                details.Recipe.OutputUnit,
                CostEstimated: false));
        }

        return items;
    }

    private static void ValidateQuantity(decimal quantity, string outputUnit)
    {
        if (!RecipeUnits.IsWeight(outputUnit) && quantity != Math.Truncate(quantity))
        {
            throw new ValidationException("Штучные позиции заказываются только целым числом.");
        }

        var max = RecipeUnits.IsWeight(outputUnit) ? MaxWeightQuantity : MaxPieceQuantity;
        if (quantity > max)
        {
            throw new ValidationException($"Слишком большое количество: не больше {max:0} {outputUnit}.");
        }
    }

    private static decimal CalculateTotal(IEnumerable<OrderItem> items) => RecipeRevenue(items);

    private static decimal CalculateTotal(
        IEnumerable<OrderItem> items,
        IEnumerable<OrderPackaging> packaging) =>
        RecipeRevenue(items) + packaging.Sum(box => decimal.Round(
            box.SalePrice * box.Quantity, 2, MidpointRounding.AwayFromZero));

    private static decimal RecipeRevenue(IEnumerable<OrderItem> items) =>
        items.Sum(item => decimal.Round(item.UnitPrice * item.Quantity, 2, MidpointRounding.AwayFromZero));

    private static decimal RecipeCost(IEnumerable<OrderItem> items) =>
        items.Sum(item => item.CostPrice * item.Quantity);

    /// <summary>Средневзвешенная наценка заказа: выручка рецептов / их себестоимость.</summary>
    private static decimal MarkupFactor(IReadOnlyList<OrderItem> items)
    {
        var cost = RecipeCost(items);
        return cost > 0m ? RecipeRevenue(items) / cost : 1m;
    }

    private static decimal RoundMoney(decimal value) =>
        decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    private static decimal RoundUpTotal(decimal value) => Math.Ceiling(value);

    private static void InsertOrder(SqliteConnection connection, SqliteTransaction transaction, Order order)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO orders
                (id, number, first_name, last_name, phone, customer_id, created_at, due_date, status, comment,
                 total_price_kopecks, discount_kopecks, paid_amount_kopecks, updated_at)
            VALUES
                ($id, $number, $first, $last, $phone, $customer, $created, $due, $status, $comment,
                 $total, $discount, $paid, $updated);
            """;
        command.Parameters.AddWithValue("$id", order.Id.ToString());
        command.Parameters.AddWithValue("$number", order.Number);
        command.Parameters.AddWithValue("$first", order.FirstName);
        command.Parameters.AddWithValue("$last", (object?)order.LastName ?? DBNull.Value);
        command.Parameters.AddWithValue("$phone", order.Phone);
        command.Parameters.AddWithValue("$customer", (object?)order.CustomerId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", order.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$due", order.DueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$status", order.Status.ToString());
        command.Parameters.AddWithValue("$comment", (object?)order.Comment ?? DBNull.Value);
        command.Parameters.AddWithValue("$total", ToKopecks(order.TotalPrice));
        command.Parameters.AddWithValue("$discount", ToKopecks(order.DiscountAmount));
        command.Parameters.AddWithValue("$paid", ToKopecks(order.PaidAmount));
        command.Parameters.AddWithValue("$updated", order.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    private static void InsertItems(SqliteConnection connection, SqliteTransaction transaction, Order order)
    {
        foreach (var item in order.Items)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO order_items
                    (id, order_id, recipe_id, recipe_name, quantity_milli, unit_price_kopecks,
                     cost_price_kopecks, cost_estimated, output_quantity_milli, output_unit)
                VALUES
                    ($id, $order, $recipe, $name, $quantity, $price, $cost, $estimated, $output, $unit);
                """;
            command.Parameters.AddWithValue("$id", item.Id.ToString());
            command.Parameters.AddWithValue("$order", order.Id.ToString());
            command.Parameters.AddWithValue("$recipe", (object?)item.RecipeId?.ToString() ?? DBNull.Value);
            command.Parameters.AddWithValue("$name", item.RecipeName);
            command.Parameters.AddWithValue("$quantity", ToMilli(item.Quantity));
            command.Parameters.AddWithValue("$price", ToKopecks(item.UnitPrice));
            command.Parameters.AddWithValue("$cost", ToKopecks(item.CostPrice));
            command.Parameters.AddWithValue("$estimated", item.CostEstimated ? 1 : 0);
            command.Parameters.AddWithValue("$output", ToMilli(item.OutputQuantity));
            command.Parameters.AddWithValue("$unit", item.OutputUnit);
            command.ExecuteNonQuery();
        }
    }

    private static void InsertPackaging(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid orderId,
        OrderPackaging box,
        int position)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO order_packaging
                (id, order_id, packaging_id, name, size, cost_kopecks, sale_price_kopecks, quantity, position)
            VALUES
                ($id, $order, $packaging, $name, $size, $cost, $sale, $quantity, $position);
            """;
        command.Parameters.AddWithValue("$id", box.Id.ToString());
        command.Parameters.AddWithValue("$order", orderId.ToString());
        command.Parameters.AddWithValue("$packaging", (object?)box.PackagingId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$name", box.Name);
        command.Parameters.AddWithValue("$size", (object?)box.Size ?? DBNull.Value);
        command.Parameters.AddWithValue("$cost", ToKopecks(box.CostPrice));
        command.Parameters.AddWithValue("$sale", ToKopecks(box.SalePrice));
        command.Parameters.AddWithValue("$quantity", box.Quantity);
        command.Parameters.AddWithValue("$position", position);
        command.ExecuteNonQuery();
    }

    private static List<Order> LoadOrders(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, number, first_name, last_name, phone, customer_id,
                   created_at, due_date, status, comment, total_price_kopecks, discount_kopecks,
                   paid_amount_kopecks, updated_at
            FROM orders;
            """;

        var result = new List<Order>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(ReadOrder(reader));
        }

        return result;
    }

    private static Order? FindOrder(SqliteConnection connection, Guid orderId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, number, first_name, last_name, phone, customer_id,
                   created_at, due_date, status, comment, total_price_kopecks, discount_kopecks,
                   paid_amount_kopecks, updated_at
            FROM orders
            WHERE id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", orderId.ToString());

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadOrder(reader) : null;
    }

    private static Order ReadOrder(SqliteDataReader reader)
    {
        var statusText = reader.GetString(8);
        if (!Enum.TryParse<OrderStatus>(statusText, out var status))
        {
            status = OrderStatus.New;
        }

        return new Order(
            Guid.Parse(reader.GetString(0)),
            reader.GetInt32(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5)),
            ParseDate(reader.GetString(6)),
            DateOnly.ParseExact(reader.GetString(7), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            status,
            reader.IsDBNull(9) ? null : reader.GetString(9),
            Array.Empty<OrderItem>(),
            Array.Empty<OrderPackaging>(),
            FromKopecks(reader.GetInt64(10)),
            FromKopecks(reader.GetInt64(11)),
            FromKopecks(reader.GetInt64(12)),
            ParseDate(reader.GetString(13)));
    }

    private static Dictionary<Guid, List<OrderItem>> LoadItems(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, order_id, recipe_id, recipe_name, quantity_milli, unit_price_kopecks,
                   cost_price_kopecks, cost_estimated, output_quantity_milli, output_unit
            FROM order_items;
            """;

        var result = new Dictionary<Guid, List<OrderItem>>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var orderId = Guid.Parse(reader.GetString(1));
            if (!result.TryGetValue(orderId, out var list))
            {
                list = new List<OrderItem>();
                result[orderId] = list;
            }

            list.Add(new OrderItem(
                Guid.Parse(reader.GetString(0)),
                reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                reader.GetString(3),
                FromMilli(reader.GetInt64(4)),
                FromKopecks(reader.GetInt64(5)),
                FromKopecks(reader.GetInt64(6)),
                FromMilli(reader.GetInt64(8)),
                reader.GetString(9),
                reader.GetInt32(7) != 0));
        }

        return result;
    }

    private static Dictionary<Guid, List<OrderPackaging>> LoadPackaging(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, order_id, packaging_id, name, size, cost_kopecks, sale_price_kopecks, quantity
            FROM order_packaging
            ORDER BY order_id, position;
            """;

        var result = new Dictionary<Guid, List<OrderPackaging>>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var orderId = Guid.Parse(reader.GetString(1));
            if (!result.TryGetValue(orderId, out var list))
            {
                list = new List<OrderPackaging>();
                result[orderId] = list;
            }

            list.Add(new OrderPackaging(
                Guid.Parse(reader.GetString(0)),
                reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                FromKopecks(reader.GetInt64(5)),
                FromKopecks(reader.GetInt64(6)),
                reader.GetInt32(7)));
        }

        return result;
    }

    private static int GetNextNumber(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(number), 0) + 1 FROM orders;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static (string FirstName, string? LastName, string Phone) ValidateClient(
        string? firstName,
        string? lastName,
        string? phone)
    {
        var first = (firstName ?? string.Empty).Trim();
        if (first.Length == 0)
        {
            throw new ValidationException("Укажите имя клиента.");
        }

        if (first.Length > MaxNameLength)
        {
            throw new ValidationException("Имя длиннее 60 символов — сократите.");
        }

        var last = (lastName ?? string.Empty).Trim();
        if (last.Length > MaxNameLength)
        {
            throw new ValidationException("Фамилия длиннее 60 символов — сократите.");
        }

        var cleanedPhone = (phone ?? string.Empty).Trim();
        if (cleanedPhone.Length == 0)
        {
            throw new ValidationException("Укажите телефон клиента.");
        }

        if (cleanedPhone.Length > MaxPhoneLength)
        {
            throw new ValidationException("Телефон длиннее 30 символов — сократите.");
        }

        return (first, last.Length == 0 ? null : last, cleanedPhone);
    }

    private static void ValidateDueDate(DateOnly dueDate)
    {
        if (dueDate < DateOnly.FromDateTime(DateTime.Now))
        {
            throw new ValidationException("Дата выдачи не может быть в прошлом.");
        }
    }

    private static string? CleanComment(string? comment)
    {
        var trimmed = (comment ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (trimmed.Length > MaxCommentLength)
        {
            throw new ValidationException("Комментарий длиннее 300 символов — сократите.");
        }

        return trimmed;
    }

    private static long ToKopecks(decimal rubles) =>
        (long)decimal.Round(rubles * 100m, 0, MidpointRounding.AwayFromZero);

    private static decimal FromKopecks(long kopecks) => kopecks / 100m;

    private static long ToMilli(decimal quantity) =>
        (long)decimal.Round(quantity * 1000m, 0, MidpointRounding.AwayFromZero);

    private static decimal FromMilli(long milli) => milli / 1000m;

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
