using Avalonia;

namespace RigolUI;

internal static class Program
{
    /// <summary>Command line the window uses for its optional auto-start.</summary>
    public static string[] StartupArgs { get; private set; } = [];

    [STAThread]
    public static int Main(string[] args)
    {
        StartupArgs = args;

        // Headless smoke test of the acquisition/mask engine, no window involved.
        if (args.Contains("--check"))
            return SessionCheck.Run(args);

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
