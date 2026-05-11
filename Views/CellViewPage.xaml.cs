using Microsoft.UI.Xaml.Controls;
using BMSMonitor.ViewModels;

namespace BMSMonitor.Views;

public sealed partial class CellViewPage : Page
{
    private MainViewModel ViewModel => App.ViewModel;
    private Services.LocalizationManager Lang => App.Lang;

    public CellViewPage()
    {
        InitializeComponent();
    }
}
