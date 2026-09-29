namespace YoutubeMusicController.Tray;

internal static class Program
{
    private const string MutexName =
        "Local\\YoutubeMusicController.Tray";

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        using var instanceMutex = new Mutex(
            initiallyOwned: true,
            MutexName,
            out var isFirstInstance);

        if (!isFirstInstance)
        {
            MessageBox.Show(
                "YouTube Music Controller is already running in the system tray.",
                "YouTube Music Controller",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        Application.Run(new TrayApplicationContext());
    }
}
