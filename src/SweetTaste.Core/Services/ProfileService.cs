using System.Globalization;
using Microsoft.Data.Sqlite;
using SweetTaste.Core.Domain;
using SweetTaste.Core.Storage;

namespace SweetTaste.Core.Services;

/// <summary>Профиль кондитера: одна запись в базе, фото хранится рядом с данными и попадает в бэкап.</summary>
public sealed class ProfileService
{
    private const int MaxNameLength = 80;
    private const int MaxLinkLength = 200;
    private const int MaxPhotoBytes = 4_000_000;

    private readonly AppDatabase _database;

    public ProfileService(AppDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _database.Initialize();
    }

    public string ExportDatabase(string destinationPath) => _database.ExportTo(destinationPath);

    public ConfectionerProfile GetProfile()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name, owner_name, instagram, telegram, website, photo, updated_at
            FROM confectioner_profile
            WHERE id = 1
            LIMIT 1;
            """;

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return new ConfectionerProfile(string.Empty, string.Empty, string.Empty, string.Empty,
                string.Empty, null, DateTimeOffset.Now);
        }

        return new ConfectionerProfile(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : (byte[])reader[5],
            ParseDate(reader.GetString(6)));
    }

    /// <summary>Сохраняет текстовые поля и фото одним действием. null в photo удаляет картинку.</summary>
    public ConfectionerProfile SaveProfile(
        string? name,
        string? ownerName,
        string? instagram,
        string? telegram,
        string? website,
        byte[]? photo)
    {
        var cleanName = Clean(name, MaxNameLength, "Название");
        var cleanOwner = Clean(ownerName, MaxNameLength, "Имя");
        var cleanInstagram = Clean(instagram, MaxLinkLength, "Instagram");
        var cleanTelegram = Clean(telegram, MaxLinkLength, "Telegram");
        var cleanWebsite = Clean(website, MaxLinkLength, "Сайт");

        if (photo is { Length: > MaxPhotoBytes })
        {
            throw new ValidationException("Фото слишком большое — выберите файл поменьше.");
        }

        var now = DateTimeOffset.Now;
        var profile = new ConfectionerProfile(
            cleanName,
            cleanOwner,
            cleanInstagram,
            cleanTelegram,
            cleanWebsite,
            photo is { Length: 0 } ? null : photo,
            now);

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO confectioner_profile
                (id, name, owner_name, instagram, telegram, website, photo, updated_at)
            VALUES
                (1, $name, $owner, $instagram, $telegram, $website, $photo, $updated)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                owner_name = excluded.owner_name,
                instagram = excluded.instagram,
                telegram = excluded.telegram,
                website = excluded.website,
                photo = excluded.photo,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$name", profile.Name);
        command.Parameters.AddWithValue("$owner", profile.OwnerName);
        command.Parameters.AddWithValue("$instagram", profile.Instagram);
        command.Parameters.AddWithValue("$telegram", profile.Telegram);
        command.Parameters.AddWithValue("$website", profile.Website);
        command.Parameters.AddWithValue("$photo", (object?)profile.Photo ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated", profile.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();

        return profile;
    }

    private static string Clean(string? value, int maxLength, string field)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length > maxLength)
        {
            throw new ValidationException($"{field}: не длиннее {maxLength} символов.");
        }

        return trimmed;
    }

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
