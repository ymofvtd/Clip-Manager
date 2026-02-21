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
            // Check if another instance is already running
            bool isNewInstance = false;
            singleInstanceMutex = new Mutex(true, "VideoTrayApp_SingleInstance", out isNewInstance);

            if (!isNewInstance)
            {
                // Another instance is running - show notification and exit
                ShowNotification();
                return;
            }

            try
            {
                // To customize application configuration such as set high DPI settings or default font,
                // see https://aka.ms/applicationconfiguration.
                ApplicationConfiguration.Initialize();
                Application.Run(new Form1());
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
                notifyIcon.Icon = SystemIcons.Application;
                notifyIcon.Visible = true;
                notifyIcon.ShowBalloonTip(
                    3000,
                    "Video Duration Tracker",
                    "Already running! Refreshed duration.",
                    ToolTipIcon.Info
                );
                System.Threading.Thread.Sleep(3500);
            }
        }
    }
}