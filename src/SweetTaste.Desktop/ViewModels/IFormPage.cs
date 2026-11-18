namespace SweetTaste.Desktop.ViewModels;

/// <summary>Раздел с модальными панелями: Escape закрывает верхнюю панель.</summary>
public interface IFormPage
{
    bool IsFormOpen { get; }

    void CancelForm();
}
