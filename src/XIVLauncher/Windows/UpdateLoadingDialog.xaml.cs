using System.Windows.Input;

namespace XIVLauncher.Windows;

public partial class UpdateLoadingDialog
{
    public UpdateLoadingDialog()
    {
        InitializeComponent();
        
        DataContext =  new();
        MouseMove   += UpdateLoadingDialog_OnMouseMove;
    }

    private void UpdateLoadingDialog_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }
}
