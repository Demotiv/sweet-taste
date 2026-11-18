using System.Reflection;

namespace SweetTaste.Desktop;

/// <summary>Версия приложения из метаданных сборки.</summary>
public static class AppInfo
{
    public static string Version { get; } = ResolveVersion();

    public static string VersionLine => "Версия " + Version;

    private static string ResolveVersion()
    {
        var assembly = typeof(AppInfo).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
