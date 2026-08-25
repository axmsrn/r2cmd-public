using System.Windows;

namespace R2Cmd;

public partial class App : Application
{
    // Keep a reference to the Mutex so it doesn't get garbage collected
    private static Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        const string mutexName = "R2Cmd_SingleInstance_Mutex_v1";

        // Try to create a new Mutex. If it already exists, createdNew will be false.
        _instanceMutex = new Mutex(true, mutexName, out bool createdNew);

        if (!createdNew)
        {
            // Another instance is already running. Shut down this new one immediately.
            Current.Shutdown();
            return;
        }

        base.OnStartup(e);

        DispatcherUnhandledException += (s, args) =>
        {
            MessageBox.Show(args.Exception.Message, "Unexpected Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
    }
}
