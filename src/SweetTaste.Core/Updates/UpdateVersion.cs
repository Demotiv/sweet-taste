using System.Globalization;

namespace SweetTaste.Core.Updates;

/// <summary>
/// Версия приложения в формате SemVer с поддержкой pre-release: 0.1.0-alpha.2.
/// Сравнение нужно для проверки обновлений на GitHub.
/// </summary>
public readonly record struct UpdateVersion(int Major, int Minor, int Patch, string? PreRelease)
    : IComparable<UpdateVersion>
{
    public static bool TryParse(string? value, out UpdateVersion version)
    {
        version = default;

        var text = (value ?? string.Empty).Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        var plus = text.IndexOf('+');
        if (plus >= 0)
        {
            text = text[..plus];
        }

        string? preRelease = null;
        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            preRelease = text[(dash + 1)..];
            text = text[..dash];

            if (preRelease.Length == 0)
            {
                return false;
            }
        }

        var parts = text.Split('.');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return false;
        }

        version = new UpdateVersion(major, minor, patch, preRelease);
        return true;
    }

    public int CompareTo(UpdateVersion other)
    {
        var result = Major.CompareTo(other.Major);
        if (result != 0)
        {
            return result;
        }

        result = Minor.CompareTo(other.Minor);
        if (result != 0)
        {
            return result;
        }

        result = Patch.CompareTo(other.Patch);
        if (result != 0)
        {
            return result;
        }

        return ComparePreRelease(PreRelease, other.PreRelease);
    }

    public override string ToString() =>
        $"{Major}.{Minor}.{Patch}" + (string.IsNullOrWhiteSpace(PreRelease) ? string.Empty : "-" + PreRelease);

    private static int ComparePreRelease(string? left, string? right)
    {
        if (left is null && right is null)
        {
            return 0;
        }

        // Обычная версия новее pre-release: 0.1.0 > 0.1.0-alpha.2.
        if (left is null)
        {
            return 1;
        }

        if (right is null)
        {
            return -1;
        }

        var leftParts = left.Split('.');
        var rightParts = right.Split('.');
        var length = Math.Min(leftParts.Length, rightParts.Length);

        for (var index = 0; index < length; index++)
        {
            var result = CompareIdentifier(leftParts[index], rightParts[index]);
            if (result != 0)
            {
                return result;
            }
        }

        return leftParts.Length.CompareTo(rightParts.Length);
    }

    private static int CompareIdentifier(string left, string right)
    {
        var leftNumeric = int.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber);
        var rightNumeric = int.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber);

        if (leftNumeric && rightNumeric)
        {
            return leftNumber.CompareTo(rightNumber);
        }

        // Числовой идентификатор ниже буквенного по SemVer.
        if (leftNumeric)
        {
            return -1;
        }

        if (rightNumeric)
        {
            return 1;
        }

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
