using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using PosSystem.Frontend.ViewModels;

namespace PosSystem.Frontend.Views;

public partial class LoginView : UserControl
{
    private PasswordBox? _pinBox;
    private bool _updatingPin;

    public LoginView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
    }

#if MANUAL_XAML_LOADER
    public void InitializeComponent()
    {
        XamlComponentLoader.LoadUserControl(this, "/PosSystem.Frontend;component/views/loginview.xaml", "PosSystem.Frontend.Views.LoginView.xaml");
    }
#endif

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        WirePinBox();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is INotifyPropertyChanged oldVm)
        {
            oldVm.PropertyChanged -= ViewModel_PropertyChanged;
        }
        if (e.NewValue is INotifyPropertyChanged newVm)
        {
            newVm.PropertyChanged += ViewModel_PropertyChanged;
        }
        WirePinBox();
    }

    private void WirePinBox()
    {
        _pinBox ??= LogicalTreeHelper.FindLogicalNode(this, "PinBox") as PasswordBox;
        if (_pinBox != null)
        {
            _pinBox.PasswordChanged -= PinBox_PasswordChanged;
            _pinBox.PasswordChanged += PinBox_PasswordChanged;

            if (DataContext is LoginViewModel vm && _pinBox.Password != vm.Pin)
            {
                _updatingPin = true;
                _pinBox.Password = vm.Pin ?? string.Empty;
                _updatingPin = false;
            }
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LoginViewModel.Pin) && !_updatingPin && DataContext is LoginViewModel vm)
        {
            _pinBox ??= LogicalTreeHelper.FindLogicalNode(this, "PinBox") as PasswordBox;
            if (_pinBox != null && _pinBox.Password != vm.Pin)
            {
                _updatingPin = true;
                _pinBox.Password = vm.Pin ?? string.Empty;
                _updatingPin = false;
            }
        }
    }

    private void PinBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_updatingPin) return;
        if (DataContext is LoginViewModel vm && sender is PasswordBox pb)
        {
            _updatingPin = true;
            vm.Pin = pb.Password;
            _updatingPin = false;
        }
    }
}
