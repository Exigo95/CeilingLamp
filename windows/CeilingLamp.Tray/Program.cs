namespace CeilingLamp;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Only one copy may run: the light accepts a single connection.
        using var mutex = new Mutex(true, "CeilingLamp.Tray.SingleInstance", out var first);
        if (!first) return;

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayApp(startedByWindows: args.Contains("--tray")));
    }
}
