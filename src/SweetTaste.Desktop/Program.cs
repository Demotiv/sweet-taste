using System.Globalization;
using System.IO;
using Avalonia;

namespace SweetTaste.Desktop;

internal sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "sweet-taste-crash.log");
            File.AppendAllText(path, $"[{DateTime.Now:O}] {args.ExceptionObject}{Environment.NewLine}");
        }
        catch
        {
            // Логирование не должно мешать обработке ошибки.
        }
    }
}
