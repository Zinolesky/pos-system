using System.Windows;
using System.Windows.Input;
using PosSystem.Frontend.Hardware;
using PosSystem.Frontend.ViewModels;

namespace PosSystem.Frontend;

public partial class MainWindow : Window
{
    private readonly BarcodeScannerListener _scannerListener;

    public MainWindow(MainViewModel viewModel, BarcodeScannerListener scannerListener)
    {
        InitializeComponent();
        DataContext = viewModel;
        _scannerListener = scannerListener;
        _scannerListener.StartListening();

        PreviewTextInput += MainWindow_PreviewTextInput;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
    }

#if MANUAL_XAML_LOADER
    public void InitializeComponent()
    {
        XamlComponentLoader.LoadWindow(this, "/PosSystem.Frontend;component/mainwindow.xaml", "PosSystem.Frontend.MainWindow.xaml");
    }
#endif

    private void MainWindow_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Text))
        {
            _scannerListener.ProcessWedgeChar(e.Text[0]);
        }
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (_scannerListener.TryCompleteWedgeScan())
            {
                e.Handled = true;
                return;
            }
        }

        if (DataContext is MainViewModel mainVm && mainVm.CurrentViewModel is CheckoutViewModel checkoutVm)
        {
            switch (e.Key)
            {
                case Key.F2:
                    checkoutVm.ActiveSection = "Register";
                    e.Handled = true;
                    break;
                case Key.F4:
                    checkoutVm.HoldOrRecallSaleCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.F5:
                    checkoutVm.QuickExactCashCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.F8:
                    checkoutVm.ActiveSection = "Customers";
                    e.Handled = true;
                    break;
                case Key.F9:
                    checkoutVm.LockStationCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.F12:
                    checkoutVm.PayCommand.Execute(null);
                    e.Handled = true;
                    break;
            }
        }
    }
}
