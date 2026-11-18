using System.Globalization;
using Microsoft.Data.Sqlite;
using SweetTaste.Core.Storage;

namespace SweetTaste.Core.Domain;

/// <summary>Профиль кондитера: название, имя, соцсети и логотип.</summary>
public sealed record ConfectionerProfile(
    string Name,
    string OwnerName,
    string Instagram,
    string Telegram,
    string Website,
    byte[]? Photo,
    DateTimeOffset UpdatedAt);
