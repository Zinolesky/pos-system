using System.Windows.Controls;

namespace PosSystem.Frontend.Views;

public partial class CheckoutView : UserControl
{
    public CheckoutView()
    {
        InitializeComponent();
    }

#if MANUAL_XAML_LOADER
    public void InitializeComponent()
    {
        XamlComponentLoader.LoadUserControl(this, "/PosSystem.Frontend;component/views/checkoutview.xaml", "PosSystem.Frontend.Views.CheckoutView.xaml");
    }
#endif
}
