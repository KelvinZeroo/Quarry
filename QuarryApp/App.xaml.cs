using System.Configuration;
using System.Data;
using System.Windows;

namespace QuarryApp;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        string? initialArg = null;
        if (e.Args.Length > 0 && !string.IsNullOrWhiteSpace(e.Args[0]))
        {
            initialArg = e.Args[0];
        }

        var mainWindow = new MainWindow(initialArg);
        MainWindow = mainWindow;
        mainWindow.Show();
    }
}

