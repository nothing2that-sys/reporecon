using System.Windows;
using System.Windows.Media;

namespace RepoRecon.App;

/// <summary>
/// C# access to the semantic color tokens defined once in App.xaml (§2).
/// ViewModels set Foreground/Background as plain hex strings (bound directly to
/// Brush-typed dependency properties), so this resolves the *same* App.xaml
/// resources to hex rather than duplicating the palette as a second literal set.
/// </summary>
public static class AppColors
{
    public static string Hex(string resourceKey)
    {
        if (Application.Current?.Resources[resourceKey] is not SolidColorBrush brush)
        {
            throw new InvalidOperationException($"Color resource '{resourceKey}' not found in App.xaml.");
        }

        var c = brush.Color;
        return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    public static string Success => Hex("SuccessForeground");
    public static string SuccessBackground => Hex("SuccessBackground");
    public static string SuccessBorder => Hex("SuccessBorder");

    public static string Warning => Hex("WarningForeground");
    public static string WarningBackground => Hex("WarningBackground");
    public static string WarningBorder => Hex("WarningBorder");

    public static string Error => Hex("ErrorForeground");
    public static string ErrorBackground => Hex("ErrorBackground");
    public static string ErrorBorder => Hex("ErrorBorder");

    public static string Info => Hex("InfoForeground");
    public static string InfoBackground => Hex("InfoBackground");
    public static string InfoBorder => Hex("InfoBorder");

    public static string Neutral => Hex("NeutralForeground");
    public static string NeutralMuted => Hex("NeutralForegroundMuted");
    public static string NeutralBorder => Hex("NeutralBorder");
    public static string NeutralSurface => Hex("NeutralSurface");
}
