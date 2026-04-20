using System.Windows;
using System.Windows.Input;
using XIVLauncher.Common.Game;
using XIVLauncher.Windows.ViewModel;

namespace XIVLauncher.Windows;

/// <summary>
///     Interaction logic for FirstTimeSetup.xaml
/// </summary>
public partial class IntegrityCheckProgressWindow : Window
{
    private IntegrityCheckProgressWindowViewModel ViewModel => (IntegrityCheckProgressWindowViewModel)DataContext;

    public IntegrityCheckProgressWindow()
    {
        InitializeComponent();

        DataContext = new IntegrityCheckProgressWindowViewModel();

        MouseMove += IntegrityCheckProgressWindow_OnMouseMove;
    }

    public void UpdateProgress(IntegrityCheckProgress progress) =>
        ViewModel.CurrentFile = progress.CurrentFile;

    private void IntegrityCheckProgressWindow_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }
}
