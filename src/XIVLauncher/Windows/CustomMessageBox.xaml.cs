using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MaterialDesignThemes.Wpf;
using Serilog;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Util;
using XIVLauncher.Support;
using XIVLauncher.Windows.ViewModel;
using XIVLauncher.Xaml;

namespace XIVLauncher.Windows;

/// <summary>
///     Interaction logic for CustomMessageBox.xaml
/// </summary>
public partial class CustomMessageBox
{
    public const string ERROR_EXPLANATION = "XIVLauncher 发生错误, 请查阅常见问题\n如果问题仍然存在, 请点击下方按钮在 GitHub 上报告此问题, 描述问题并复制文本框中的内容";

    private readonly Builder _builder;

    private CustomMessageBoxViewModel ViewModel => (DataContext as CustomMessageBoxViewModel)!;

    private MessageBoxResult result;
    private bool             isClosing;

    private CustomMessageBox(Builder builder)
    {
        _builder = builder;
        result   = _builder.CancelResult;

        InitializeComponent();

        var viewModel = new CustomMessageBoxViewModel();
        viewModel.ApplyBuilder(builder);
        DataContext = viewModel;

        ViewModel.CopyMessageTextCommand = new SyncCommand(p => Clipboard.SetText(_builder.Text));

        if (builder.ParentWindow?.IsVisible ?? false)
        {
            Owner         = builder.ParentWindow;
            ShowInTaskbar = false;
        }
        else
            ShowInTaskbar = true;

        if (_builder.ShowInputTextBox)
        {
            InputTextBox.Visibility = Visibility.Visible;
            InputTextBox.Text       = _builder.InputTextBoxText ?? string.Empty;
        }
        else
            InputTextBox.Visibility = Visibility.Collapsed;

        Title                 = builder.Caption;
        MessageTextBlock.Text = builder.Text;

        if (string.IsNullOrWhiteSpace(builder.Description))
            DescriptionTextBox.Visibility = Visibility.Collapsed;
        else
        {
            DescriptionTextBox.Document.Blocks.Clear();
            DescriptionTextBox.Document.Blocks.Add(new Paragraph(new Run(builder.Description)));
        }

        switch (builder.Buttons)
        {
            case MessageBoxButton.OK:
                Button1.Content    = builder.OkButtonText ?? "确定";
                Button2.Visibility = Visibility.Collapsed;
                Button3.Visibility = Visibility.Collapsed;
                (builder.DefaultResult switch
                        {
                            MessageBoxResult.OK => Button1,
                            _                   => throw new ArgumentOutOfRangeException(nameof(builder.DefaultResult), builder.DefaultResult, null)
                        }).Focus();
                break;

            case MessageBoxButton.OKCancel:
                Button1.Content    = builder.OkButtonText     ?? "确定";
                Button2.Content    = builder.CancelButtonText ?? "取消";
                Button3.Visibility = Visibility.Collapsed;
                (builder.DefaultResult switch
                        {
                            MessageBoxResult.OK     => Button1,
                            MessageBoxResult.Cancel => Button2,
                            _                       => throw new ArgumentOutOfRangeException(nameof(builder.DefaultResult), builder.DefaultResult, null)
                        }).Focus();
                break;

            case MessageBoxButton.YesNoCancel:
                Button1.Content = builder.YesButtonText    ?? "是";
                Button2.Content = builder.NoButtonText     ?? "否";
                Button3.Content = builder.CancelButtonText ?? "取消";
                (builder.DefaultResult switch
                        {
                            MessageBoxResult.Yes    => Button1,
                            MessageBoxResult.No     => Button2,
                            MessageBoxResult.Cancel => Button3,
                            _                       => throw new ArgumentOutOfRangeException(nameof(builder.DefaultResult), builder.DefaultResult, null)
                        }).Focus();
                break;

            case MessageBoxButton.YesNo:
                Button1.Content    = builder.YesButtonText ?? "是";
                Button2.Content    = builder.NoButtonText  ?? "否";
                Button3.Visibility = Visibility.Collapsed;
                (builder.DefaultResult switch
                        {
                            MessageBoxResult.Yes => Button1,
                            MessageBoxResult.No  => Button2,
                            _                    => throw new ArgumentOutOfRangeException(nameof(builder.DefaultResult), builder.DefaultResult, null)
                        }).Focus();

                if (builder.YesCountDownSeconds > 0)
                {
                    Button1.IsEnabled = false;
                    var countdown = builder.YesCountDownSeconds;
                    Task.Run
                    (async () =>
                        {
                            while (countdown > 0)
                            {
                                await Task.Delay(1000);
                                countdown -= 1;
                                if (countdown <= 0)
                                    break;
                                Dispatcher.Invoke(() => Button1.Content = $"{builder.YesButtonText ?? "是"} ({countdown})");
                            }

                            Dispatcher.Invoke
                            (() =>
                                {
                                    Button1.IsEnabled = true;
                                    Button1.Content   = builder.YesButtonText ?? "是";
                                }
                            );
                        }
                    );
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(builder.Buttons), builder.Buttons, null);
        }

        switch (builder.Image)
        {
            case MessageBoxImage.None:
                ErrorPackIcon.Visibility = Visibility.Collapsed;
                break;

            case MessageBoxImage.Hand:
                ErrorPackIcon.Visibility = Visibility.Visible;
                ErrorPackIcon.Kind       = PackIconKind.Error;
                ErrorPackIcon.Foreground = Brushes.Red;
                SystemSounds.Hand.Play();
                break;

            case MessageBoxImage.Question:
                ErrorPackIcon.Visibility = Visibility.Visible;
                ErrorPackIcon.Kind       = PackIconKind.QuestionMarkCircle;
                ErrorPackIcon.Foreground = Brushes.DarkOrange;
                SystemSounds.Question.Play();
                break;

            case MessageBoxImage.Exclamation:
                ErrorPackIcon.Visibility = Visibility.Visible;
                ErrorPackIcon.Kind       = PackIconKind.Warning;
                ErrorPackIcon.Foreground = Brushes.Yellow;
                SystemSounds.Exclamation.Play();
                break;

            case MessageBoxImage.Asterisk:
                ErrorPackIcon.Visibility = Visibility.Visible;
                ErrorPackIcon.Kind       = PackIconKind.Information;
                ErrorPackIcon.Foreground = Brushes.DarkOrange;
                SystemSounds.Asterisk.Play();
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(builder.Image), builder.Image, null);
        }

        OfficialLauncherButton.Visibility = builder.ShowOfficialLauncher ? Visibility.Visible : Visibility.Collapsed;
        DiscordButton.Visibility          = builder.ShowDiscordLink ? Visibility.Visible : Visibility.Collapsed;
        IntegrityReportButton.Visibility  = builder.ShowIntegrityReportLinks ? Visibility.Visible : Visibility.Collapsed;
        NewGitHubIssueButton.Visibility   = builder.ShowNewGitHubIssue ? Visibility.Visible : Visibility.Collapsed;

        Topmost = builder.OverrideTopMostFromParentWindow ? builder.ParentWindow?.Topmost ?? builder.TopMost : builder.TopMost;

        Closing += CustomMessageBox_Closing;
    }

    public static MessageBoxResult Show
    (
        string           text,
        string           caption,
        MessageBoxButton buttons              = MessageBoxButton.OK,
        MessageBoxImage  image                = MessageBoxImage.Asterisk,
        bool             showHelpLinks        = true,
        bool             showDiscordLink      = true,
        bool             showReportLinks      = false,
        bool             showOfficialLauncher = false,
        Window           parentWindow         = null!
    )
    {
        return new Builder()
               .WithCaption(caption)
               .WithText(text)
               .WithButtons(buttons)
               .WithImage(image)
               .WithShowHelpLinks(showHelpLinks)
               .WithShowDiscordLink(showDiscordLink)
               .WithShowIntegrityReportLink(showReportLinks)
               .WithShowOfficialLauncher(showOfficialLauncher)
               .WithParentWindow(parentWindow)
               .Show();
    }

    public static bool AssertOrShowError(bool condition, string context, bool fatal = false, Window parentWindow = null!)
    {
        if (condition)
            return false;

        try
        {
            throw new InvalidOperationException("Assertion failure.");
        }
        catch (Exception e)
        {
            Builder.NewFrom(e, context, fatal ? ExitOnCloseModes.ExitOnClose : ExitOnCloseModes.DontExitOnClose)
                   .WithAppendText("\n\n")
                   .WithAppendText("发生了不可能发生的事情")
                   .WithParentWindow(parentWindow)
                   .Show();
        }

        return true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Close();

        base.OnKeyDown(e);
    }

    // https://docs.microsoft.com/en-us/archive/blogs/twistylittlepassagesallalike/everyone-quotes-command-line-arguments-the-wrong-way
    private static string EncodeParameterArgument(string argument, bool force = false)
    {
        if (!force && argument.Length > 0 && argument.IndexOfAny(" \t\n\v\"".ToCharArray()) == -1)
            return argument;

        var quoted = new StringBuilder(argument.Length * 2);
        quoted.Append('"');

        var numberBackslashes = 0;

        foreach (var chr in argument)
        {
            switch (chr)
            {
                case '\\':
                    numberBackslashes++;
                    continue;

                case '"':
                    quoted.Append('\\', numberBackslashes * 2 + 1);
                    quoted.Append(chr);
                    break;

                default:
                    quoted.Append('\\', numberBackslashes);
                    quoted.Append(chr);
                    break;
            }

            numberBackslashes = 0;
        }

        quoted.Append('\\', numberBackslashes * 2);
        quoted.Append('"');

        return quoted.ToString();
    }

    private void CustomMessageBox_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && !Equals(e.Source, DescriptionTextBox))
            DragMove();
    }

    private void CustomMessageBox_Closing(object? sender, CancelEventArgs e)
    {
        if (isClosing)
            return;

        e.Cancel  = true;
        isClosing = true;

        if (FindResource("WindowCloseAnimation") is Storyboard storyboard)
        {
            var closeStoryboard = storyboard.Clone();
            closeStoryboard.Completed += (_, _) => Close();
            closeStoryboard.Begin(this);
        }
        else Close();
    }

    private void Button1_Click(object sender, RoutedEventArgs e)
    {
        result = _builder.Buttons switch
        {
            MessageBoxButton.OK          => MessageBoxResult.OK,
            MessageBoxButton.OKCancel    => MessageBoxResult.OK,
            MessageBoxButton.YesNoCancel => MessageBoxResult.Yes,
            MessageBoxButton.YesNo       => MessageBoxResult.Yes,
            _                            => throw new NotImplementedException()
        };
        if (_builder.ShowInputTextBox)
            _builder.InputTextBoxText = InputTextBox.Text;
        Close();
    }

    private void Button2_Click(object sender, RoutedEventArgs e)
    {
        result = _builder.Buttons switch
        {
            MessageBoxButton.OKCancel    => MessageBoxResult.Cancel,
            MessageBoxButton.YesNoCancel => MessageBoxResult.No,
            MessageBoxButton.YesNo       => MessageBoxResult.No,
            _                            => throw new NotImplementedException()
        };
        Close();
    }

    private void Button3_Click(object sender, RoutedEventArgs e)
    {
        result = _builder.Buttons switch
        {
            MessageBoxButton.YesNoCancel => MessageBoxResult.Cancel,
            _                            => throw new NotImplementedException()
        };
        Close();
    }

    private void OfficialLauncherButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.Settings.GamePath == null || !GameHelpers.GetOfficialLauncherPath(App.Settings.GamePath).Exists)
        {
            Show
            (
                "没有设置游戏安装路径, XIVLauncher 无法启动官方启动器",
                "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error,
                parentWindow: this
            );
            return;
        }

        GameHelpers.StartOfficialLauncher(App.Settings.GamePath);

        Environment.Exit(0);
    }

    private void DiscordButton_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(Links.DISCORD_URL) { UseShellExecute = true });

    private void IntegrityReportButton_Click(object sender, RoutedEventArgs e) =>
        Process.Start(Path.Combine(Paths.RoamingPath, "integrityreport.txt"));

    private void NewGitHubIssueButton_OnClick(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(Links.REPO_URL) { UseShellExecute = true });

    private void PackTroubleshooting_OnClick(object sender, RoutedEventArgs e) =>
        PackGenerator.PackAndShowMessage(this);

    public enum ExitOnCloseModes
    {
        DontExitOnClose,
        ExitOnClose
    }

    public class Builder
    {
        internal string           Text;
        internal string           Caption = "XIVLauncherCN (Soil)";
        internal string           Description;
        internal MessageBoxButton Buttons       = MessageBoxButton.OK;
        internal MessageBoxResult DefaultResult = MessageBoxResult.None; // On enter
        internal MessageBoxResult CancelResult  = MessageBoxResult.None; // On escape
        internal MessageBoxImage  Image         = MessageBoxImage.None;
        internal string           OkButtonText;
        internal string           CancelButtonText;
        internal string           YesButtonText;
        internal string           NoButtonText;
        internal bool             TopMost;
        internal ExitOnCloseModes ExitOnCloseMode = ExitOnCloseModes.DontExitOnClose;
        internal bool             ShowHelpLinks;
        internal bool             ShowDiscordLink;
        internal bool             ShowIntegrityReportLinks;
        internal bool             ShowOfficialLauncher;
        internal bool             ShowNewGitHubIssue;
        internal bool             ShowTroubleshootingPackButton;
        internal Window           ParentWindow;
        internal bool             OverrideTopMostFromParentWindow = true;
        internal float            YesCountDownSeconds;
        internal bool             ShowInputTextBox;
        internal string           InputTextBoxText = string.Empty;

        public static Builder NewFrom(string text) => new Builder().WithText(text);

        public static Builder NewFrom(Exception exc, string context, ExitOnCloseModes exitOnCloseMode = ExitOnCloseModes.DontExitOnClose)
        {
            var builder = new Builder()
                          .WithText(ERROR_EXPLANATION)
                          .WithExitOnClose(exitOnCloseMode)
                          .WithImage(MessageBoxImage.Error)
                          .WithShowHelpLinks()
                          .WithShowDiscordLink()
                          .WithShowTroubleshootingPackButton()
                          .WithShowNewGitHubIssue()
                          .WithAppendDescription(exc.ToString())
                          .WithAppendSettingsDescription(context);

            if (exitOnCloseMode == ExitOnCloseModes.ExitOnClose)
            {
                builder.WithButtons(MessageBoxButton.YesNo)
                       .WithYesButtonText("重新启动")
                       .WithNoButtonText("退出");
            }

            return builder;
        }

        public static Builder NewFromUnexpectedException(Exception exc, string context, ExitOnCloseModes exitOnCloseMode = ExitOnCloseModes.DontExitOnClose)
        {
            return NewFrom(exc, context, exitOnCloseMode)
                   .WithAppendTextFormatted($"发生未知错误 ({exc.Message})")
                   .WithAppendText("\n")
                   .WithAppendText("请反馈此错误");
        }

        public Builder WithText(string text)
        {
            Text = text;
            return this;
        }

        public Builder WithTextFormatted(string format, params object[] args)
        {
            Text = string.Format(format, args);
            return this;
        }

        public Builder WithAppendText(string text)
        {
            Text = (Text ?? "") + text;
            return this;
        }

        public Builder WithAppendTextFormatted(string format, params object[] args)
        {
            Text = (Text ?? "") + string.Format(format, args);
            return this;
        }

        public Builder WithCaption(string caption)
        {
            Caption = caption;
            return this;
        }

        public Builder WithDescription(string description)
        {
            Description = description;
            return this;
        }

        public Builder WithAppendDescription(string description)
        {
            Description = (Description ?? "") + description;
            return this;
        }

        public Builder WithButtons(MessageBoxButton buttons)
        {
            Buttons = buttons;
            return this;
        }

        public Builder WithDefaultResult(MessageBoxResult result)
        {
            DefaultResult = result;
            return this;
        }

        public Builder WithCancelResult(MessageBoxResult result)
        {
            CancelResult = result;
            return this;
        }

        public Builder WithImage(MessageBoxImage image)
        {
            Image = image;
            return this;
        }

        public Builder WithTopMost(bool topMost = true)
        {
            TopMost = topMost;
            return this;
        }

        public Builder WithExitOnClose(ExitOnCloseModes exitOnCloseMode = ExitOnCloseModes.ExitOnClose)
        {
            ExitOnCloseMode = exitOnCloseMode;
            return this;
        }

        public Builder WithOkButtonText(string text)
        {
            OkButtonText = text;
            return this;
        }

        public Builder WithCancelButtonText(string text)
        {
            CancelButtonText = text;
            return this;
        }

        public Builder WithYesButtonText(string text)
        {
            YesButtonText = text;
            return this;
        }

        public Builder WithNoButtonText(string text)
        {
            NoButtonText = text;
            return this;
        }

        public Builder WithShowHelpLinks(bool showHelpLinks = true)
        {
            ShowHelpLinks = showHelpLinks;
            return this;
        }

        public Builder WithShowDiscordLink(bool showDiscordLink = true)
        {
            ShowDiscordLink = showDiscordLink;
            return this;
        }

        public Builder WithShowOfficialLauncher(bool showOfficialLauncher = true)
        {
            ShowOfficialLauncher = showOfficialLauncher;
            return this;
        }

        public Builder WithShowIntegrityReportLink(bool showReportLinks = true)
        {
            ShowIntegrityReportLinks = showReportLinks;
            return this;
        }

        public Builder WithShowNewGitHubIssue(bool showNewGitHubIssue = true)
        {
            ShowNewGitHubIssue = showNewGitHubIssue;
            return this;
        }

        public Builder WithShowTroubleshootingPackButton(bool showTroubleshootingPackButton = true)
        {
            ShowTroubleshootingPackButton = showTroubleshootingPackButton;
            return this;
        }

        public Builder WithParentWindow(Window window)
        {
            ParentWindow = window;
            return this;
        }

        public Builder WithParentWindow(Window window, bool overrideTopMost)
        {
            ParentWindow                    = window;
            OverrideTopMostFromParentWindow = overrideTopMost;
            return this;
        }

        public Builder WithInputTextBox(string text, bool showInputTextBox = true)
        {
            ShowInputTextBox = showInputTextBox;
            InputTextBoxText = text;
            return this;
        }

        public Builder WithExceptionText() =>
            WithText("XIVLauncher 发生错误, 请查阅常见问题\n如果问题仍然存在, 请点击下方按钮在 GitHub 上报告此问题, 描述问题并复制文本框中的内容");

        public Builder WithAppendSettingsDescription(string context)
        {
            WithAppendDescription("\n\n版本: "        + AppUtil.GetAssemblyVersion())
                .WithAppendDescription("\nGit 哈希: " + AppUtil.GetGitHash())
                .WithAppendDescription("\n上下文: "    + context)
                .WithAppendDescription("\n操作系统: "   + Environment.OSVersion)
                .WithAppendDescription("\n64 位: "   + Environment.Is64BitProcess);

            if (App.Settings != null)
            {
                WithAppendDescription("\n启用 Dalamud: " + App.Settings.DalamudEnabled)
                    .WithAppendDescription("\n语言: "    + App.Settings.Language)
                    .WithAppendDescription("\n启动器语言: " + App.Settings.LauncherLanguage)
                    .WithAppendDescription("\n游戏路径: "  + App.Settings.GamePath);
            }

#if DEBUG
            WithAppendDescription("\n[调试模式]");
#endif

            return this;
        }

        public Builder WithYesCountdown(float countDownSeconds)
        {
            YesCountDownSeconds = countDownSeconds;
            return this;
        }

        public MessageBoxResult ShowAssumingDispatcherThread()
        {
            DefaultResult = DefaultResult != MessageBoxResult.None
                                ? DefaultResult
                                : Buttons switch
                                {
                                    MessageBoxButton.OK          => MessageBoxResult.OK,
                                    MessageBoxButton.OKCancel    => MessageBoxResult.OK,
                                    MessageBoxButton.YesNoCancel => MessageBoxResult.Yes,
                                    MessageBoxButton.YesNo       => MessageBoxResult.Yes,
                                    _                            => throw new NotImplementedException()
                                };

            CancelResult = CancelResult != MessageBoxResult.None
                               ? CancelResult
                               : Buttons switch
                               {
                                   MessageBoxButton.OK          => MessageBoxResult.OK,
                                   MessageBoxButton.OKCancel    => MessageBoxResult.Cancel,
                                   MessageBoxButton.YesNoCancel => MessageBoxResult.Cancel,
                                   MessageBoxButton.YesNo       => MessageBoxResult.No,
                                   _                            => throw new NotImplementedException()
                               };

            var res = new CustomMessageBox(this);
            res.ShowDialog();
            return res.result;
        }

        public MessageBoxResult ShowInNewThread()
        {
            MessageBoxResult? res             = null;
            var               newWindowThread = new Thread(() => res = ShowAssumingDispatcherThread());
            newWindowThread.SetApartmentState(ApartmentState.STA);
            newWindowThread.IsBackground = true;
            newWindowThread.Start();
            newWindowThread.Join();
            return res.GetValueOrDefault(CancelResult);
        }

        public MessageBoxResult Show()
        {
            MessageBoxResult result;

            if (ParentWindow != null) result = Dispatcher.CurrentDispatcher == ParentWindow.Dispatcher ? ShowAssumingDispatcherThread() : ParentWindow.Dispatcher.Invoke(ShowAssumingDispatcherThread);
            else
            {
                if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
                    result = ShowAssumingDispatcherThread();
                else
                    result = Application.Current.Dispatcher.Invoke(ShowAssumingDispatcherThread);
            }

            if (ExitOnCloseMode == ExitOnCloseModes.ExitOnClose)
            {
                Log.CloseAndFlush();
                if (result == MessageBoxResult.Yes)
                    Process.Start(Process.GetCurrentProcess().MainModule!.FileName, string.Join(" ", Environment.GetCommandLineArgs().Skip(1).Select(x => EncodeParameterArgument(x))));
                Environment.Exit(-1);
            }

            return result;
        }
    }
}
