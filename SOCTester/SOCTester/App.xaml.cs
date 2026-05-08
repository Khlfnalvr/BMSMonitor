using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SOCTester.ViewModels;

namespace SOCTester;

public partial class App : Application
{
    public static MainWindow? CurrentWindow { get; private set; }
    public static MainViewModel ViewModel { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            CurrentWindow = new MainWindow();
            ViewModel = CurrentWindow.ViewModel;
            CurrentWindow.Activate();
        }
        catch (Exception ex)
        {
            ShowFatalError(ex);
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        ShowFatalError(e.Exception);
    }

    private static async void ShowFatalError(Exception ex)
    {
        var dialog = new ContentDialog
        {
            Title = "Startup Error",
            Content = $"{ex.GetType().Name}\n\n{ex.Message}\n\n{ex.StackTrace}",
            CloseButtonText = "OK",
            XamlRoot = CurrentWindow?.Content?.XamlRoot
        };
        try { await dialog.ShowAsync(); } catch { }
        System.Diagnostics.Debug.WriteLine($"FATAL: {ex}");
    }
}
