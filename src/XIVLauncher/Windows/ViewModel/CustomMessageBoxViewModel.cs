using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;

namespace XIVLauncher.Windows.ViewModel;

internal class CustomMessageBoxViewModel : ViewModelBase
{
    public ICommand? CopyMessageTextCommand { get; set; }

    public string MessageText
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    public string Description
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    public string InputText
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    public string Button1Text
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    public string Button2Text
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    public string Button3Text
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    public Visibility Button2Visibility { get; set; } = Visibility.Collapsed;

    public Visibility Button3Visibility { get; set; } = Visibility.Collapsed;

    public Visibility DescriptionVisibility
    {
        get;
        set => SetProperty(ref field, value);
    } = Visibility.Collapsed;

    public Visibility InputVisibility
    {
        get;
        set => SetProperty(ref field, value);
    } = Visibility.Collapsed;

    public Visibility OfficialLauncherVisibility { get; set; } = Visibility.Collapsed;

    public Visibility DiscordVisibility { get; set; } = Visibility.Collapsed;

    public Visibility IntegrityReportVisibility { get; set; } = Visibility.Collapsed;

    public Visibility NewGitHubIssueVisibility { get; set; } = Visibility.Collapsed;

    public Visibility PackTroubleshootingVisibility { get; set; } = Visibility.Collapsed;

    public Visibility IconVisibility
    {
        get;
        set => SetProperty(ref field, value);
    } = Visibility.Collapsed;

    public PackIconKind IconKind
    {
        get;
        set => SetProperty(ref field, value);
    } = PackIconKind.AlertOctagon;

    public Brush? IconBrush
    {
        get;
        set => SetProperty(ref field, value);
    }

    public bool IsPrimaryButtonEnabled
    {
        get;
        set => SetProperty(ref field, value);
    } = true;

    public MessageBoxButton Buttons { get; private set; }

    public MessageBoxResult DefaultResult { get; private set; }

    public MessageBoxResult CancelResult { get; private set; }

    public void ApplyBuilder(CustomMessageBox.Builder builder)
    {
        Buttons       = builder.Buttons;
        DefaultResult = builder.DefaultResult;
        CancelResult  = builder.CancelResult;
        MessageText   = builder.Text;
        Description   = builder.Description      ?? string.Empty;
        InputText     = builder.InputTextBoxText ?? string.Empty;

        DescriptionVisibility         = string.IsNullOrWhiteSpace(builder.Description) ? Visibility.Collapsed : Visibility.Visible;
        InputVisibility               = builder.ShowInputTextBox ? Visibility.Visible : Visibility.Collapsed;
        OfficialLauncherVisibility    = builder.ShowOfficialLauncher ? Visibility.Visible : Visibility.Collapsed;
        DiscordVisibility             = builder.ShowDiscordLink ? Visibility.Visible : Visibility.Collapsed;
        IntegrityReportVisibility     = builder.ShowIntegrityReportLinks ? Visibility.Visible : Visibility.Collapsed;
        NewGitHubIssueVisibility      = builder.ShowNewGitHubIssue ? Visibility.Visible : Visibility.Collapsed;
        PackTroubleshootingVisibility = builder.ShowTroubleshootingPackButton ? Visibility.Visible : Visibility.Collapsed;

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
