using System.Windows.Input;
using XIVLauncher.Windows.ViewModel;

namespace XIVLauncher.Windows;

public partial class UpdateLoadingDialog
{
    public UpdateLoadingDialog()
    {
        InitializeComponent();

        DataContext =  new UpdateLoadingDialogViewModel();
        MouseMove   += UpdateLoadingDialog_OnMouseMove;
    }

    private void UpdateLoadingDialog_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }
}
