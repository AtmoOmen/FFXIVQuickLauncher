using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using XIVLauncher.Windows.ViewModel;

namespace XIVLauncher.Windows;

public partial class GameClientFileTaskWindow
{
    private bool isClosingAnimated;

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
        if (isClosingAnimated)
            return;

        e.Cancel = true;

        if (ViewModel.IsRunning)
        {
            ViewModel.RequestClose();
        }
        else
        {
            ViewModel.RequestClose();
            isClosingAnimated = true;
            PlayCloseAnimationAndClose();
        }
    }

    private void PlayCloseAnimationAndClose()
    {
        if (FindResource("WindowCloseAnimation") is System.Windows.Media.Animation.Storyboard storyboard)
        {
            storyboard.Completed += (s, ev) => Close();
            storyboard.Begin(this);
        }
        else
        {
            Close();
        }
    }
}
