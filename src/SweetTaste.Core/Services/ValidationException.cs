namespace SweetTaste.Core.Services;

/// <summary>Ошибка бизнес-правила. Текст показывается пользователю как есть.</summary>
public sealed class ValidationException : Exception
{
    public ValidationException(string message)
        : base(message)
    {
    }
}
