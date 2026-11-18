namespace SweetTaste.Core.Domain;

/// <summary>Клиент кондитера: номер, имя, фамилия и телефон.</summary>
public sealed record Customer(
    Guid Id,
    int Number,
    string FirstName,
    string? LastName,
    string Phone,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public string FullName => string.IsNullOrWhiteSpace(LastName)
        ? FirstName
        : $"{FirstName} {LastName}";
}

/// <summary>Нормализация телефона для поиска и склейки клиентов.</summary>
public static class PhoneNumbers
{
    public static string Normalize(string? phone)
    {
        var digits = new string((phone ?? string.Empty).Where(char.IsDigit).ToArray());

        if (digits.Length == 10)
        {
            digits = "7" + digits;
        }
        else if (digits.Length == 11 && digits[0] == '8')
        {
            digits = "7" + digits[1..];
        }

        return digits.Length > 0 ? digits : (phone ?? string.Empty).Trim().ToLowerInvariant();
    }
}
