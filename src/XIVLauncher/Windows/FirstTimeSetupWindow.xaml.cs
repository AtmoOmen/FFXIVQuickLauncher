using System;
using System.IO;
using System.Windows;
using CheapLoc;
using Serilog;
using XIVLauncher.Common;
using XIVLauncher.Common.Util;
using XIVLauncher.Windows.ViewModel;

namespace XIVLauncher.Windows;

/// <summary>
///     Interaction logic for FirstTimeSetup.xaml
/// </summary>
public partial class FirstTimeSetup : Window
{
    public bool WasCompleted { get; private set; }

    public FirstTimeSetup()
    {
        InitializeComponent();

        DataContext = new FirstTimeSetupViewModel();

        var detectedPath = AppUtil.TryGamePaths();

        if (detectedPath != null) GamePathEntry.Text = detectedPath;

        try
        {
            var desktop       = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); //获取桌面文件夹路径
            var directoryInfo = new DirectoryInfo(Environment.CurrentDirectory).Parent;
            if (directoryInfo != null)
                CreateShortcut(desktop, "XIVLauncherCN (Soil)", Path.Combine(directoryInfo.FullName, "XIVLauncherCN.exe"));
        }
        catch
        {
            CustomMessageBox.Show
            (
                "创建快捷方式失败，如需要请手动创建快捷方式到桌面。",
                "XIVLauncherCN (Soil)",
                MessageBoxButton.OK,
                MessageBoxImage.Exclamation,
                parentWindow: this
            );
        }
    }

    public static void CreateShortcut
    (
        string directory,
        string shortcutName,
        string targetPath,
        string description  = null,
        string iconLocation = null
    )
    {
        if (!Directory.Exists(directory))
        {
            if (directory != null)
                Directory.CreateDirectory(directory);
        }

        if (directory != null)
        {
            var     shortcutPath = Path.Combine(directory, $"{shortcutName}.lnk");
            var     shellType    = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell        = Activator.CreateInstance(shellType);
            var     shortcut     = shell.CreateShortcut(shortcutPath);                                       //创建快捷方式对象
            shortcut.TargetPath       = targetPath;                                                          //指定目标路径
            shortcut.WorkingDirectory = Path.GetDirectoryName(targetPath);                                   //设置起始位置
            shortcut.WindowStyle      = 1;                                                                   //设置运行方式，默认为常规窗口
            shortcut.Description      = description;                                                         //设置备注
            shortcut.IconLocation     = string.IsNullOrWhiteSpace(iconLocation) ? targetPath : iconLocation; //设置图标路径
            shortcut.Save();                                                                                 //保存快捷方式
        }
    }

    public static string GetShortcutTargetFile(string path)
    {
        var     shellType = Type.GetTypeFromProgID("WScript.Shell");
        dynamic shell     = Activator.CreateInstance(shellType);
        var     shortcut  = shell.CreateShortcut(path);

        return shortcut.TargetPath;
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        Log.Information($"[FirstTimeSetup] 当前步骤索引: {SetupTabControl.SelectedIndex}");

        switch (SetupTabControl.SelectedIndex)
        {
            case 0 when string.IsNullOrEmpty(GamePathEntry.Text):
                CustomMessageBox.Show
                (
                    Loc.Localize("GamePathEmptyError", "Please select a game path."),
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error,
                    false,
                    false,
                    parentWindow: this
                );
                return;

            case 0 when !GameHelpers.LetChoosePath(GamePathEntry.Text):
                CustomMessageBox.Show
                (
                    Loc.Localize("GamePathSafeguardError", "Please do not select the \"game\" or \"boot\" folder of your game installation, and choose the folder that contains these instead."),
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error,
                    parentWindow: this
                );
                return;

            case 0:
            {
                if (!GameHelpers.IsValidGamePath(GamePathEntry.Text))
                {
                    if (CustomMessageBox.Show
                        (
                            Loc.Localize("GamePathInvalidConfirm", "The folder you selected has no installation of the game.\nXIVLauncher will install the game the first time you log in.\nContinue?"),
                            "XIVLauncherCN (Soil)",
                            MessageBoxButton.YesNo,
                            parentWindow: this
                        )
                        != MessageBoxResult.Yes)
                        return;
                }

                if (GamePathEntry.Text.StartsWith('C'))
                {
                    if (CustomMessageBox.Show
                        (
                            "你选择的游戏路径位于 C 盘\nXIVLauncherCN 无法正常登陆, 请将游戏移出 C 盘或者使用管理员启动 XIVLauncherCN",
                            "XIVLauncherCN (Soil)",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Warning,
                            parentWindow: this
                        )
                        != MessageBoxResult.Yes)
                        return;
                }

                break;
            }

            case 1:
                App.Settings.GamePath           = new(GamePathEntry.Text);
                App.Settings.Language           = ClientLanguage.ChineseSimplified;
                App.Settings.InGameAddonEnabled = HooksCheckBox.IsChecked == true;

                App.Settings.AddonList = [];

                WasCompleted = true;
                Close();
                break;
        }

        SetupTabControl.SelectedIndex++;
    }
}
