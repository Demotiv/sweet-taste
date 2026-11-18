using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using SweetTaste.Desktop.ViewModels;

namespace SweetTaste.Desktop.Views;

public partial class MainWindow : Window
{
    private double _uiScale = 1.0;

    public MainWindow()
    {
        InitializeComponent();
        ActualThemeVariantChanged += (_, _) =>
            Dispatcher.UIThread.Post(ApplyTitleBarTheme, DispatcherPriority.Loaded);
    }

    /// <summary>Масштабирует содержимое окна и подстраивает размер под экран.</summary>
    public void ApplyUiScale(double scale)
    {
        _uiScale = scale;
        ScaleHost.LayoutTransform = new ScaleTransform(scale, scale);

        if (Screens.Primary is not { } screen)
        {
            return;
        }

        var workWidth = screen.WorkingArea.Width / screen.Scaling;
        var workHeight = screen.WorkingArea.Height / screen.Scaling;
        var width = 1340 * scale;
        var height = 840 * scale;

        if (width > workWidth || height > workHeight)
        {
            WindowState = WindowState.Maximized;
            return;
        }

        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
        }

        Width = width;
        Height = height;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        ApplyUiScale(_uiScale);
        Dispatcher.UIThread.Post(ApplyTitleBarTheme, DispatcherPriority.Loaded);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape &&
            DataContext is MainWindowViewModel { CurrentPage: IFormPage { IsFormOpen: true } formPage })
        {
            formPage.CancelForm();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    /// <summary>Красит системный заголовок окна под тёмную тему Windows.</summary>
    private void ApplyTitleBarTheme()
    {
        if (!OperatingSystem.IsWindows() || TryGetPlatformHandle() is not { } platform)
        {
            return;
        }

        var dark = ActualThemeVariant == ThemeVariant.Dark ? 1 : 0;
        _ = DwmSetWindowAttribute(platform.Handle, 20, ref dark, sizeof(int));
        _ = DwmSetWindowAttribute(platform.Handle, 19, ref dark, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
