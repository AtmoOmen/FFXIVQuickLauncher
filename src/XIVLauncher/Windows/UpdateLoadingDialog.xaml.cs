using System.Windows;
using System.Windows.Input;
using XIVLauncher.Windows.ViewModel;

namespace XIVLauncher.Windows;

public partial class UpdateLoadingDialog
{
    public UpdateLoadingDialog()
    {
        InitializeComponent();

        AutoLoginDisclaimer.Visibility     = App.Settings.AutologinEnabled ? Visibility.Visible : Visibility.Collapsed;

        DataContext = new UpdateLoadingDialogViewModel();
        MouseMove += UpdateLoadingDialog_OnMouseMove;
    }

    private void UpdateLoadingDialog_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }
}
