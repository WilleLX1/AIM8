using System.Windows;
using System.Windows.Threading;

namespace AIM8.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A background failure should land in the console, not close the window mid-game.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (MainWindow is MainWindow main)
        {
            main.ReportUnhandled(e.Exception);
            e.Handled = true;
            return;
        }

        MessageBox.Show(e.Exception.ToString(), "AIM8", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
