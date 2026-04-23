using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using XIVLauncher.Windows.ViewModel;

namespace XIVLauncher.Windows;

public partial class GameClientFileTaskWindow : Window
{
    private GameClientFileTaskWindowViewModel ViewModel => (GameClientFileTaskWindowViewModel)DataContext;

    public GameClientFileTaskWindow(GameClientFileTaskWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        MouseMove += GameClientFileTaskWindow_OnMouseMove;
    }

    private void GameClientFileTaskWindow_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void GameClientFileTaskWindow_OnClosing(object sender, CancelEventArgs e)
    {
        if (!ViewModel.IsRunning)
        {
            ViewModel.RequestClose();
            return;
        }

        e.Cancel = true;
        ViewModel.RequestClose();
    }
}
