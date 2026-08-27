using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MaterialDesignThemes.Wpf;

namespace XIVLauncher.Windows.ViewModel;

internal partial class CustomMessageBoxViewModel : ObservableObject
{
    public ICommand? CopyMessageTextCommand { get; set; }

    [ObservableProperty]
    public partial string MessageText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Description { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string InputText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Button1Text { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Button2Text { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Button3Text { get; set; } = string.Empty;

    public Visibility Button2Visibility { get; set; } = Visibility.Collapsed;

    public Visibility Button3Visibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial Visibility DescriptionVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial Visibility InputVisibility { get; set; } = Visibility.Collapsed;

    public Visibility OfficialLauncherVisibility { get; set; } = Visibility.Collapsed;

    public Visibility DiscordVisibility { get; set; } = Visibility.Collapsed;

    public Visibility IntegrityReportVisibility { get; set; } = Visibility.Collapsed;

    public Visibility NewGitHubIssueVisibility { get; set; } = Visibility.Collapsed;

    public Visibility PackTroubleshootingVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial Visibility IconVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial PackIconKind IconKind { get; set; } = PackIconKind.AlertOctagon;

    [ObservableProperty]
    public partial Brush? IconBrush { get; set; }

    [ObservableProperty]
    public partial bool IsPrimaryButtonEnabled { get; set; } = true;

    public MessageBoxButton Buttons { get; private set; }

    public MessageBoxResult DefaultResult { get; private set; }

    public MessageBoxResult CancelResult { get; private set; }

    public void ApplyBuilder
    (
        CustomMessageBox.Builder builder
    )
    {
        Buttons       = builder.Buttons;
        DefaultResult = builder.DefaultResult;
        CancelResult  = builder.CancelResult;
        MessageText   = builder.Text;
        Description   = builder.Description      ?? string.Empty;
        InputText     = builder.InputTextBoxText ?? string.Empty;

        DescriptionVisibility = string.IsNullOrWhiteSpace(builder.Description) ?
                                    Visibility.Collapsed :
                                    Visibility.Visible;
        InputVisibility = builder.ShowInputTextBox ?
                              Visibility.Visible :
                              Visibility.Collapsed;
        OfficialLauncherVisibility = builder.ShowOfficialLauncher ?
                                         Visibility.Visible :
                                         Visibility.Collapsed;
        DiscordVisibility = builder.ShowDiscordLink ?
                                Visibility.Visible :
                                Visibility.Collapsed;
        IntegrityReportVisibility = builder.ShowIntegrityReportLinks ?
                                        Visibility.Visible :
                                        Visibility.Collapsed;
        NewGitHubIssueVisibility = builder.ShowNewGitHubIssue ?
                                       Visibility.Visible :
                                       Visibility.Collapsed;
        PackTroubleshootingVisibility = builder.ShowTroubleshootingPackButton ?
                                            Visibility.Visible :
                                            Visibility.Collapsed;

        switch (builder.Image)
        {
            case MessageBoxImage.None:
                IconVisibility = Visibility.Collapsed;
                break;

            case MessageBoxImage.Hand:
                IconVisibility = Visibility.Visible;
                IconKind       = PackIconKind.Error;
                IconBrush      = Brushes.Red;
                break;

            case MessageBoxImage.Question:
                IconVisibility = Visibility.Visible;
                IconKind       = PackIconKind.QuestionMarkCircle;
                IconBrush      = Brushes.DarkOrange;
                break;

            case MessageBoxImage.Exclamation:
                IconVisibility = Visibility.Visible;
                IconKind       = PackIconKind.Warning;
                IconBrush      = Brushes.Goldenrod;
                break;

            case MessageBoxImage.Asterisk:
                IconVisibility = Visibility.Visible;
                IconKind       = PackIconKind.Information;
                IconBrush      = Brushes.DarkOrange;
                break;
        }

        switch (builder.Buttons)
        {
            case MessageBoxButton.OK:
                Button1Text       = builder.OkButtonText ?? "确定";
                Button2Visibility = Visibility.Collapsed;
                Button3Visibility = Visibility.Collapsed;
                break;

            case MessageBoxButton.OKCancel:
                Button1Text       = builder.OkButtonText     ?? "确定";
                Button2Text       = builder.CancelButtonText ?? "取消";
                Button2Visibility = Visibility.Visible;
                Button3Visibility = Visibility.Collapsed;
                break;

            case MessageBoxButton.YesNo:
                Button1Text       = builder.YesButtonText ?? "是";
                Button2Text       = builder.NoButtonText  ?? "否";
                Button2Visibility = Visibility.Visible;
                Button3Visibility = Visibility.Collapsed;
                break;

            case MessageBoxButton.YesNoCancel:
                Button1Text       = builder.YesButtonText    ?? "是";
                Button2Text       = builder.NoButtonText     ?? "否";
                Button3Text       = builder.CancelButtonText ?? "取消";
                Button2Visibility = Visibility.Visible;
                Button3Visibility = Visibility.Visible;
                break;
        }
    }
}
