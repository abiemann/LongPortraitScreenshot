using LongPortraitScreenshot.UI;

namespace LongPortraitScreenshot;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using Icon? appIcon = Icon.ExtractIcon(Application.ExecutablePath, 0);
        Application.Run(new MainForm { Icon = appIcon });
    }
}
