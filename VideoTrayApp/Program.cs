using System.Runtime.InteropServices;

namespace VideoTrayApp
{
    internal static class Program
    {
        private static Mutex? singleInstanceMutex;

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
                    // Signal the existing window, then exit without creating services.
                    using var activation = new InstanceActivation("ClipsManager_Activate");
                    activation.Signal();
                    return;
                }

                // To customize application configuration such as set high DPI settings or default font,
                // see https://aka.ms/applicationconfiguration.
                ApplicationConfiguration.Initialize();
                using var window = new MainShell();
                using var activationListener = new InstanceActivation("ClipsManager_Activate");
                window.HandleCreated += (_, _) => activationListener.Listen(window, window.ActivateExisting);
                Application.Run(window);
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
                    Directory.CreateDirectory(Path.GetDirectoryName(errorLog)!);
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

    }
}
