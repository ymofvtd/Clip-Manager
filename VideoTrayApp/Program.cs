using System.Runtime.InteropServices;

namespace VideoTrayApp
{
    internal static class Program
    {
        private static Mutex singleInstanceMutex;

        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            try
            {
                // Check if another instance is already running
                bool isNewInstance = false;
                singleInstanceMutex = new Mutex(true, "ClipsManager_SingleInstance", out isNewInstance);

                if (!isNewInstance)
                {
                    // Another instance is running - show notification and exit
                    ShowNotification();
                    return;
                }

                // To customize application configuration such as set high DPI settings or default font,
                // see https://aka.ms/applicationconfiguration.
                ApplicationConfiguration.Initialize();
                Application.Run(new Form1());
            }
            catch (Exception ex)
            {
                // Log any critical errors
                string errorLog = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "ClipsManager",
                    "error.log"
                );

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(errorLog));
                    File.AppendAllText(errorLog, $"\n[{DateTime.Now}] {ex}\n");
                }
                catch { }

                MessageBox.Show(
                    $"Application failed to start:\n\n{ex.Message}",
                    "Startup Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
            }
            finally
            {
                singleInstanceMutex?.Dispose();
            }
        }

        static void ShowNotification()
        {
            using (var notifyIcon = new NotifyIcon())
            {
                notifyIcon.Icon = LoadApplicationIcon();
                notifyIcon.Visible = true;
                notifyIcon.ShowBalloonTip(
                    3000,
                    "Clips Manager",
                    "Already running! Refreshed duration.",
                    ToolTipIcon.Info
                );
                System.Threading.Thread.Sleep(3500);
            }
        }

        static Icon LoadApplicationIcon()
        {
            try
            {
                // Try to load icon from the application directory
                string iconPath = Path.Combine(AppContext.BaseDirectory, "icon.ico");
                if (File.Exists(iconPath))
                {
                    return new Icon(iconPath);
                }

                // Try loading from current directory as fallback
                if (File.Exists("icon.ico"))
                {
                    return new Icon("icon.ico");
                }

                // If icon not found, use system application icon
                return SystemIcons.Application;
            }
            catch
            {
                // If anything fails, use system application icon
                return SystemIcons.Application;
            }
        }
    }
}