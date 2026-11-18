using System.Globalization;
using Microsoft.Data.Sqlite;
using SweetTaste.Core.Domain;
using SweetTaste.Core.Storage;

namespace SweetTaste.Core.Services;

/// <summary>
/// База клиентов: выдача номера при первом заказе, поиск для автозаполнения
/// и обновление имени, если клиент назвался иначе.
/// </summary>
public sealed class CustomerService
{
    private const int MaxNameLength = 60;
    private const int MaxPhoneLength = 30;

    private readonly AppDatabase _database;

    public CustomerService(AppDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _database.Initialize();
    }

    public IReadOnlyList<Customer> GetCustomers()
    {
        using var connection = _database.Open();
        return LoadCustomers(connection)
            .OrderBy(customer => customer.Number)
            .ToList();
    }

    /// <summary>Поиск по телефону (от 3 цифр) или имени/фамилии (от 2 символов).</summary>
    public IReadOnlyList<Customer> Search(string? query, int limit = 6)
    {
        var text = (query ?? string.Empty).Trim();
        if (text.Length < 2)
        {
            return Array.Empty<Customer>();
        }

        var digits = PhoneNumbers.Normalize(text);
        var byPhone = text.Any(char.IsDigit) && digits.Length >= 3;

        return GetCustomers()
            .Where(customer =>
                (byPhone && PhoneNumbers.Normalize(customer.Phone).Contains(digits, StringComparison.Ordinal)) ||
                customer.FullName.Contains(text, StringComparison.CurrentCultureIgnoreCase) ||
                customer.Phone.Contains(text, StringComparison.CurrentCultureIgnoreCase))
            .Take(Math.Max(1, limit))
            .ToList();
    }

    /// <summary>Находит клиента по нормализованному телефону или создаёт нового с очередным номером.</summary>
    public Customer ResolveOrCreate(string? firstName, string? lastName, string? phone)
    {
        var first = CleanName(firstName, required: true);
        var last = CleanName(lastName, required: false);
        var cleanPhone = (phone ?? string.Empty).Trim();

        if (cleanPhone.Length == 0)
        {
            throw new ValidationException("Укажите телефон клиента.");
        }

        if (cleanPhone.Length > MaxPhoneLength)
        {
            throw new ValidationException("Телефон длиннее 30 символов — сократите.");
        }

        var normalized = PhoneNumbers.Normalize(cleanPhone);

        using var connection = _database.Open();
        var existing = FindByPhone(connection, normalized);

        if (existing is not null)
        {
            if (!string.Equals(existing.FirstName, first, StringComparison.Ordinal) ||
                !string.Equals(existing.LastName, last, StringComparison.Ordinal) ||
                !string.Equals(existing.Phone, cleanPhone, StringComparison.Ordinal))
            {
                using var update = connection.CreateCommand();
                update.CommandText = """
                    UPDATE customers
                    SET first_name = $first, last_name = $last, phone = $phone, updated_at = $updated
                    WHERE id = $id;
                    """;
                update.Parameters.AddWithValue("$id", existing.Id.ToString());
                update.Parameters.AddWithValue("$first", first);
                update.Parameters.AddWithValue("$last", (object?)last ?? DBNull.Value);
                update.Parameters.AddWithValue("$phone", cleanPhone);
                update.Parameters.AddWithValue("$updated", DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
                update.ExecuteNonQuery();

                existing = existing with
                {
                    FirstName = first,
                    LastName = last,
                    Phone = cleanPhone,
                    UpdatedAt = DateTimeOffset.Now,
                };
            }

            return existing;
        }

        var now = DateTimeOffset.Now;
        var number = GetNextNumber(connection);
        var customer = new Customer(
            Guid.NewGuid(),
            number,
            first,
            last,
            cleanPhone,
            now,
            now);

        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO customers
                (id, profile_id, number, first_name, last_name, phone, phone_normalized, created_at, updated_at)
            VALUES
                ($id, 1, $number, $first, $last, $phone, $normalized, $created, $updated);
            """;
        insert.Parameters.AddWithValue("$id", customer.Id.ToString());
        insert.Parameters.AddWithValue("$number", customer.Number);
        insert.Parameters.AddWithValue("$first", customer.FirstName);
        insert.Parameters.AddWithValue("$last", (object?)customer.LastName ?? DBNull.Value);
        insert.Parameters.AddWithValue("$phone", customer.Phone);
        insert.Parameters.AddWithValue("$normalized", normalized);
        insert.Parameters.AddWithValue("$created", customer.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        insert.Parameters.AddWithValue("$updated", customer.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        insert.ExecuteNonQuery();

        return customer;
    }

    /// <summary>
    /// Удаляет клиента. Заказы остаются в истории, но теряют привязку к карточке.
    /// Возвращает число отвязанных заказов.
    /// </summary>
    public int DeleteCustomer(Guid customerId)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        int unlinked;
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE orders SET customer_id = NULL WHERE customer_id = $id;";
            update.Parameters.AddWithValue("$id", customerId.ToString());
            unlinked = update.ExecuteNonQuery();
        }

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM customers WHERE id = $id AND profile_id = 1;";
            delete.Parameters.AddWithValue("$id", customerId.ToString());

            if (delete.ExecuteNonQuery() == 0)
            {
                throw new ValidationException("Клиент не найден.");
            }
        }

        transaction.Commit();
        return unlinked;
    }

    private static List<Customer> LoadCustomers(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, number, first_name, last_name, phone, created_at, updated_at
            FROM customers
            WHERE profile_id = 1;
            """;

        var result = new List<Customer>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(ReadCustomer(reader));
        }

        return result;
    }

    private static Customer? FindByPhone(SqliteConnection connection, string normalized)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, number, first_name, last_name, phone, created_at, updated_at
            FROM customers
            WHERE profile_id = 1 AND phone_normalized = $phone
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$phone", normalized);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadCustomer(reader) : null;
    }

    private static Customer ReadCustomer(SqliteDataReader reader) =>
        new(
            Guid.Parse(reader.GetString(0)),
            reader.GetInt32(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4),
            ParseDate(reader.GetString(5)),
            ParseDate(reader.GetString(6)));

    private static int GetNextNumber(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(number), 0) + 1 FROM customers WHERE profile_id = 1;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static string CleanName(string? value, bool required)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (required && trimmed.Length == 0)
        {
            throw new ValidationException("Укажите имя клиента.");
        }

        if (trimmed.Length > MaxNameLength)
        {
            throw new ValidationException("Имя длиннее 60 символов — сократите.");
        }

        return trimmed;
    }

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
