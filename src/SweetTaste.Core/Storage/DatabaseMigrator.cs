using System.Globalization;
using Microsoft.Data.Sqlite;
using SweetTaste.Core.Domain;

namespace SweetTaste.Core.Storage;

/// <summary>Простые миграции по PRAGMA user_version. Каждая версия — отдельный шаг.</summary>
internal static class DatabaseMigrator
{
    public const int CurrentVersion = 15;

    public static void Migrate(SqliteConnection connection)
    {
        var version = GetUserVersion(connection);
        if (version >= CurrentVersion)
        {
            return;
        }

        // v7 пересобирает таблицы; внешние ключи на время миграции отключаем,
        // иначе DROP TABLE упрётся в ссылки из дочерних таблиц.
        var rebuildsTables = version < 7;
        if (rebuildsTables)
        {
            Execute(connection, null, "PRAGMA foreign_keys = OFF;");
        }

        using (var transaction = connection.BeginTransaction())
        {
            if (version < 1)
            {
                ApplyVersion1(connection, transaction);
            }

            if (version < 2)
            {
                ApplyVersion2(connection, transaction);
            }

            if (version < 3)
            {
                ApplyVersion3(connection, transaction);
            }

            if (version < 4)
            {
                ApplyVersion4(connection, transaction);
            }

            if (version < 5)
            {
                ApplyVersion5(connection, transaction);
            }

            if (version < 6)
            {
                ApplyVersion6(connection, transaction);
            }

            if (version < 7)
            {
                ApplyVersion7(connection, transaction);
            }

            if (version < 8)
            {
                ApplyVersion8(connection, transaction);
            }

            if (version < 9)
            {
                ApplyVersion9(connection, transaction);
            }

            if (version < 10)
            {
                ApplyVersion10(connection, transaction);
            }

            if (version < 11)
            {
                ApplyVersion11(connection, transaction);
            }

            if (version < 12)
            {
                ApplyVersion12(connection, transaction);
            }

            if (version < 13)
            {
                ApplyVersion13(connection, transaction);
            }

            if (version < 14)
            {
                ApplyVersion14(connection, transaction);
            }

            if (version < 15)
            {
                ApplyVersion15(connection, transaction);
            }

            SetUserVersion(connection, transaction, CurrentVersion);
            transaction.Commit();
        }

        if (rebuildsTables)
        {
            Execute(connection, null, "PRAGMA foreign_keys = ON;");
        }
    }

    private static void ApplyVersion1(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, """
            CREATE TABLE ingredients (
                id         TEXT NOT NULL PRIMARY KEY,
                name       TEXT NOT NULL,
                brand      TEXT NULL,
                unit       TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE INDEX ix_ingredients_name ON ingredients(name);

            CREATE TABLE purchases (
                id                  TEXT NOT NULL PRIMARY KEY,
                ingredient_id       TEXT NOT NULL,
                purchased_on        TEXT NOT NULL,
                store               TEXT NULL,
                quantity_milli      INTEGER NOT NULL CHECK (quantity_milli > 0),
                total_price_kopecks INTEGER NOT NULL CHECK (total_price_kopecks >= 0),
                order_id            TEXT NULL,
                note                TEXT NULL,
                created_at          TEXT NOT NULL,
                FOREIGN KEY (ingredient_id) REFERENCES ingredients(id) ON DELETE RESTRICT
            );

            CREATE INDEX ix_purchases_ingredient_date
                ON purchases(ingredient_id, purchased_on);
            """);
    }

    private static void ApplyVersion2(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, """
            CREATE TABLE recipe_categories (
                id         TEXT NOT NULL PRIMARY KEY,
                name       TEXT NOT NULL,
                created_at TEXT NOT NULL
            );

            CREATE INDEX ix_recipe_categories_name ON recipe_categories(name);

            CREATE TABLE recipes (
                id              TEXT NOT NULL PRIMARY KEY,
                name            TEXT NOT NULL,
                category_id     TEXT NULL,
                output_quantity INTEGER NOT NULL CHECK (output_quantity > 0),
                output_unit     TEXT NOT NULL,
                markup_percent  INTEGER NOT NULL CHECK (markup_percent >= 0),
                created_at      TEXT NOT NULL,
                updated_at      TEXT NOT NULL,
                FOREIGN KEY (category_id) REFERENCES recipe_categories(id) ON DELETE SET NULL
            );

            CREATE INDEX ix_recipes_name ON recipes(name);

            CREATE TABLE recipe_ingredients (
                recipe_id     TEXT NOT NULL,
                ingredient_id TEXT NOT NULL,
                amount_milli  INTEGER NOT NULL CHECK (amount_milli > 0),
                position      INTEGER NOT NULL,
                PRIMARY KEY (recipe_id, ingredient_id),
                FOREIGN KEY (recipe_id) REFERENCES recipes(id) ON DELETE CASCADE,
                FOREIGN KEY (ingredient_id) REFERENCES ingredients(id) ON DELETE RESTRICT
            );

            CREATE INDEX ix_recipe_ingredients_ingredient ON recipe_ingredients(ingredient_id);
            """);
    }

    private static void ApplyVersion3(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, """
            CREATE TABLE ingredient_categories (
                id         TEXT NOT NULL PRIMARY KEY,
                name       TEXT NOT NULL,
                created_at TEXT NOT NULL
            );

            CREATE INDEX ix_ingredient_categories_name ON ingredient_categories(name);

            ALTER TABLE ingredients ADD COLUMN category_id TEXT NULL
                REFERENCES ingredient_categories(id) ON DELETE SET NULL;
            """);
    }

    private static void ApplyVersion4(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, """
            ALTER TABLE ingredients ADD COLUMN deleted_at TEXT NULL;
            ALTER TABLE recipes ADD COLUMN deleted_at TEXT NULL;

            CREATE INDEX ix_ingredients_deleted_at ON ingredients(deleted_at);
            CREATE INDEX ix_recipes_deleted_at ON recipes(deleted_at);
            """);
    }

    private static void ApplyVersion5(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, """
            CREATE TABLE orders (
                id                  TEXT NOT NULL PRIMARY KEY,
                number              INTEGER NOT NULL,
                first_name          TEXT NOT NULL,
                last_name           TEXT NULL,
                phone               TEXT NOT NULL,
                created_at          TEXT NOT NULL,
                due_date            TEXT NOT NULL,
                status              TEXT NOT NULL,
                comment             TEXT NULL,
                total_price_kopecks INTEGER NOT NULL,
                updated_at          TEXT NOT NULL
            );

            CREATE UNIQUE INDEX ux_orders_number ON orders(number);
            CREATE INDEX ix_orders_status_due ON orders(status, due_date);

            CREATE TABLE order_items (
                id                  TEXT NOT NULL PRIMARY KEY,
                order_id            TEXT NOT NULL,
                recipe_id           TEXT NULL,
                recipe_name         TEXT NOT NULL,
                quantity            INTEGER NOT NULL CHECK (quantity > 0),
                unit_price_kopecks  INTEGER NOT NULL,
                output_quantity     INTEGER NOT NULL,
                output_unit         TEXT NOT NULL,
                FOREIGN KEY (order_id) REFERENCES orders(id) ON DELETE CASCADE,
                FOREIGN KEY (recipe_id) REFERENCES recipes(id) ON DELETE SET NULL
            );

            CREATE INDEX ix_order_items_order ON order_items(order_id);
            """);
    }

    private static void ApplyVersion6(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, """
            ALTER TABLE order_items ADD COLUMN cost_price_kopecks INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE order_items ADD COLUMN cost_estimated INTEGER NOT NULL DEFAULT 1;

            -- Старые заказы: себестоимость по текущим средним ценам, приблизительно.
            UPDATE order_items
            SET cost_price_kopecks = CAST(ROUND(
                COALESCE((
                    SELECT SUM(ri.amount_milli * COALESCE(average.unit_price, 0.0))
                    FROM recipe_ingredients ri
                    LEFT JOIN (
                        SELECT ingredient_id,
                               SUM(total_price_kopecks) * 1.0 / SUM(quantity_milli) AS unit_price
                        FROM purchases
                        GROUP BY ingredient_id
                    ) average ON average.ingredient_id = ri.ingredient_id
                    WHERE ri.recipe_id = order_items.recipe_id
                ), 0.0) / NULLIF(order_items.output_quantity, 0)
            ) AS INTEGER)
            WHERE recipe_id IS NOT NULL;
            """);
    }

    /// <summary>
    /// Выход рецепта и количество в заказе становятся дробными (хранятся в тысячных),
    /// «порции» превращаются в штуки. Таблицы пересобираются, чтобы убрать целочисленные колонки.
    /// </summary>
    private static void ApplyVersion7(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, """
            CREATE TABLE recipes_v7 (
                id                    TEXT NOT NULL PRIMARY KEY,
                name                  TEXT NOT NULL,
                category_id           TEXT NULL,
                output_quantity_milli INTEGER NOT NULL CHECK (output_quantity_milli > 0),
                output_unit           TEXT NOT NULL,
                markup_percent        INTEGER NOT NULL CHECK (markup_percent >= 0),
                created_at            TEXT NOT NULL,
                updated_at            TEXT NOT NULL,
                deleted_at            TEXT NULL,
                FOREIGN KEY (category_id) REFERENCES recipe_categories(id) ON DELETE SET NULL
            );

            INSERT INTO recipes_v7
                (id, name, category_id, output_quantity_milli, output_unit,
                 markup_percent, created_at, updated_at, deleted_at)
            SELECT id, name, category_id,
                   MAX(1000, CAST(ROUND(output_quantity * 1000.0) AS INTEGER)),
                   CASE WHEN output_unit IN ('порция', 'порции') THEN 'шт' ELSE output_unit END,
                   markup_percent, created_at, updated_at, deleted_at
            FROM recipes;

            DROP TABLE recipes;
            ALTER TABLE recipes_v7 RENAME TO recipes;

            CREATE INDEX ix_recipes_name ON recipes(name);
            CREATE INDEX ix_recipes_deleted_at ON recipes(deleted_at);

            CREATE TABLE order_items_v7 (
                id                    TEXT NOT NULL PRIMARY KEY,
                order_id              TEXT NOT NULL,
                recipe_id             TEXT NULL,
                recipe_name           TEXT NOT NULL,
                quantity_milli        INTEGER NOT NULL CHECK (quantity_milli > 0),
                unit_price_kopecks    INTEGER NOT NULL,
                cost_price_kopecks    INTEGER NOT NULL DEFAULT 0,
                cost_estimated        INTEGER NOT NULL DEFAULT 1,
                output_quantity_milli INTEGER NOT NULL CHECK (output_quantity_milli > 0),
                output_unit           TEXT NOT NULL,
                FOREIGN KEY (order_id) REFERENCES orders(id) ON DELETE CASCADE,
                FOREIGN KEY (recipe_id) REFERENCES recipes(id) ON DELETE SET NULL
            );

            INSERT INTO order_items_v7
                (id, order_id, recipe_id, recipe_name, quantity_milli, unit_price_kopecks,
                 cost_price_kopecks, cost_estimated, output_quantity_milli, output_unit)
            SELECT id, order_id, recipe_id, recipe_name,
                   MAX(1000, CAST(ROUND(quantity * 1000.0) AS INTEGER)),
                   unit_price_kopecks, cost_price_kopecks, cost_estimated,
                   MAX(1000, CAST(ROUND(output_quantity * 1000.0) AS INTEGER)),
                   CASE WHEN output_unit IN ('порция', 'порции') THEN 'шт' ELSE output_unit END
            FROM order_items;

            DROP TABLE order_items;
            ALTER TABLE order_items_v7 RENAME TO order_items;

            CREATE INDEX ix_order_items_order ON order_items(order_id);
            """);
    }

    private static void ApplyVersion8(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, """
            CREATE TABLE packaging_categories (
                id         TEXT NOT NULL PRIMARY KEY,
                name       TEXT NOT NULL,
                created_at TEXT NOT NULL
            );

            CREATE INDEX ix_packaging_categories_name ON packaging_categories(name);

            CREATE TABLE packaging_items (
                id            TEXT NOT NULL PRIMARY KEY,
                category_id   TEXT NULL,
                name          TEXT NOT NULL,
                size          TEXT NULL,
                price_kopecks INTEGER NOT NULL CHECK (price_kopecks >= 0),
                created_at    TEXT NOT NULL,
                updated_at    TEXT NOT NULL,
                FOREIGN KEY (category_id) REFERENCES packaging_categories(id) ON DELETE SET NULL
            );

            CREATE INDEX ix_packaging_items_name ON packaging_items(name);

            CREATE TABLE order_packaging (
                id                   TEXT NOT NULL PRIMARY KEY,
                order_id             TEXT NOT NULL,
                packaging_id         TEXT NULL,
                name                 TEXT NOT NULL,
                size                 TEXT NULL,
                cost_kopecks         INTEGER NOT NULL CHECK (cost_kopecks >= 0),
                sale_price_kopecks   INTEGER NOT NULL CHECK (sale_price_kopecks >= 0),
                quantity             INTEGER NOT NULL CHECK (quantity > 0),
                position             INTEGER NOT NULL,
                FOREIGN KEY (order_id) REFERENCES orders(id) ON DELETE CASCADE,
                FOREIGN KEY (packaging_id) REFERENCES packaging_items(id) ON DELETE SET NULL
            );

            CREATE INDEX ix_order_packaging_order ON order_packaging(order_id);
            """);
    }

    private static void ApplyVersion9(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, """
            CREATE TABLE app_settings (
                key   TEXT NOT NULL PRIMARY KEY,
                value TEXT NOT NULL
            );
            """);
    }

    private static void ApplyVersion10(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, """
            CREATE TABLE confectioner_profile (
                id         INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
                name       TEXT NOT NULL DEFAULT '',
                owner_name TEXT NOT NULL DEFAULT '',
                instagram  TEXT NOT NULL DEFAULT '',
                telegram   TEXT NOT NULL DEFAULT '',
                website    TEXT NOT NULL DEFAULT '',
                photo      BLOB NULL,
                updated_at TEXT NOT NULL
            );
            """);
    }

    /// <summary>
    /// Привязывает все справочники и заказы к профилю кондитера.
    /// Пока профиль один (id = 1), поэтому данные получают profile_id = 1.
    /// </summary>
    private static void ApplyVersion11(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, """
            INSERT INTO confectioner_profile
                (id, name, owner_name, instagram, telegram, website, photo, updated_at)
            SELECT 1, '', '', '', '', '', NULL, strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            WHERE NOT EXISTS (SELECT 1 FROM confectioner_profile WHERE id = 1);

            ALTER TABLE ingredient_categories ADD COLUMN profile_id INTEGER NOT NULL DEFAULT 1;
            ALTER TABLE ingredients ADD COLUMN profile_id INTEGER NOT NULL DEFAULT 1;
            ALTER TABLE purchases ADD COLUMN profile_id INTEGER NOT NULL DEFAULT 1;
            ALTER TABLE recipe_categories ADD COLUMN profile_id INTEGER NOT NULL DEFAULT 1;
            ALTER TABLE recipes ADD COLUMN profile_id INTEGER NOT NULL DEFAULT 1;
            ALTER TABLE orders ADD COLUMN profile_id INTEGER NOT NULL DEFAULT 1;
            ALTER TABLE packaging_categories ADD COLUMN profile_id INTEGER NOT NULL DEFAULT 1;
            ALTER TABLE packaging_items ADD COLUMN profile_id INTEGER NOT NULL DEFAULT 1;

            CREATE INDEX ix_ingredients_profile ON ingredients(profile_id);
            CREATE INDEX ix_recipes_profile ON recipes(profile_id);
            CREATE INDEX ix_orders_profile ON orders(profile_id);
            CREATE INDEX ix_packaging_items_profile ON packaging_items(profile_id);
            """);
    }

    private static void ApplyVersion12(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, """
            CREATE TABLE customers (
                id               TEXT NOT NULL PRIMARY KEY,
                profile_id       INTEGER NOT NULL DEFAULT 1,
                number           INTEGER NOT NULL,
                first_name       TEXT NOT NULL,
                last_name        TEXT NULL,
                phone            TEXT NOT NULL,
                phone_normalized TEXT NOT NULL,
                created_at       TEXT NOT NULL,
                updated_at       TEXT NOT NULL
            );

            CREATE UNIQUE INDEX ux_customers_profile_phone ON customers(profile_id, phone_normalized);
            CREATE UNIQUE INDEX ux_customers_profile_number ON customers(profile_id, number);

            ALTER TABLE orders ADD COLUMN customer_id TEXT NULL;
            CREATE INDEX ix_orders_customer ON orders(customer_id);
            """);

        BackfillCustomers(connection, transaction);
    }

    /// <summary>Создаёт клиентов из существующих заказов и связывает заказы по нормализованному телефону.</summary>
    private static void BackfillCustomers(SqliteConnection connection, SqliteTransaction transaction)
    {
        var rows = new List<(Guid Id, string FirstName, string? LastName, string Phone, string CreatedAt)>();

        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT id, first_name, last_name, phone, created_at
                FROM orders
                ORDER BY created_at;
                """;

            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((
                    Guid.Parse(reader.GetString(0)),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4)));
            }
        }

        var number = 0;
        var now = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture);

        foreach (var group in rows
                     .GroupBy(row => PhoneNumbers.Normalize(row.Phone))
                     .OrderBy(group => group.Min(row => row.CreatedAt)))
        {
            number++;
            var ordered = group.OrderByDescending(row => row.CreatedAt).ToList();
            var latest = ordered[0];
            var earliest = ordered[^1];
            var customerId = Guid.NewGuid();

            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO customers
                        (id, profile_id, number, first_name, last_name, phone, phone_normalized, created_at, updated_at)
                    VALUES
                        ($id, 1, $number, $first, $last, $phone, $normalized, $created, $updated);
                    """;
                insert.Parameters.AddWithValue("$id", customerId.ToString());
                insert.Parameters.AddWithValue("$number", number);
                insert.Parameters.AddWithValue("$first", latest.FirstName);
                insert.Parameters.AddWithValue("$last", (object?)latest.LastName ?? DBNull.Value);
                insert.Parameters.AddWithValue("$phone", latest.Phone);
                insert.Parameters.AddWithValue("$normalized", PhoneNumbers.Normalize(latest.Phone));
                insert.Parameters.AddWithValue("$created", earliest.CreatedAt);
                insert.Parameters.AddWithValue("$updated", now);
                insert.ExecuteNonQuery();
            }

            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE orders SET customer_id = $customer WHERE id = $order;";

            var customerParameter = update.Parameters.Add("$customer", SqliteType.Text);
            customerParameter.Value = customerId.ToString();
            var orderParameter = update.Parameters.Add("$order", SqliteType.Text);

            foreach (var row in group)
            {
                orderParameter.Value = row.Id.ToString();
                update.ExecuteNonQuery();
            }
        }
    }

    private static void ApplyVersion13(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, """
            ALTER TABLE orders ADD COLUMN paid_amount_kopecks INTEGER NOT NULL DEFAULT 0;

            -- Старые выданные заказы считаем оплаченными: до этого учёта оплаты не было.
            UPDATE orders
            SET paid_amount_kopecks = total_price_kopecks
            WHERE status = 'Done';
            """);
    }

    private static void ApplyVersion14(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, """
            ALTER TABLE orders ADD COLUMN discount_kopecks INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE recipes ADD COLUMN technology TEXT NULL;
            ALTER TABLE recipes ADD COLUMN photo BLOB NULL;
            """);
    }

    /// <summary>Старые итоговые суммы округляются вверх до рубля; оплата приводится в соответствие.</summary>
    private static void ApplyVersion15(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, """
            UPDATE orders
            SET total_price_kopecks = ((total_price_kopecks + 99) / 100) * 100;

            UPDATE orders
            SET paid_amount_kopecks = total_price_kopecks
            WHERE status = 'Done';

            UPDATE orders
            SET paid_amount_kopecks = MIN(paid_amount_kopecks, total_price_kopecks);
            """);
    }

    private static int GetUserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void SetUserVersion(SqliteConnection connection, SqliteTransaction transaction, int version)
    {
        Execute(connection, transaction, $"PRAGMA user_version = {version};");
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
