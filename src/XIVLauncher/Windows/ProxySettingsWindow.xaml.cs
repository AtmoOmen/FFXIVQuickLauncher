using System.ComponentModel;
using System.Windows;
using Serilog;
using XIVLauncher.Common.Constant;
using XIVLauncher.Settings;
using XIVLauncher.Windows.ViewModel;

namespace XIVLauncher.Windows;

public partial class ProxySettingsWindow
{
    private ProxySettingsWindowViewModel ViewModel =>
        (ProxySettingsWindowViewModel)DataContext;

    public ProxySettingsWindow()
    {
        InitializeComponent();

        var settings  = ProxySettingsStore.Load(Paths.GetProxyConfigPath());
        var viewModel = new ProxySettingsWindowViewModel(settings);
        DataContext   = viewModel;

        // 切换预设时清空密码框, 防止给上一个预设输入的密码被写入新选中的预设
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProxySettingsWindowViewModel.SelectedItem))
                ProxyPasswordBox.Password = string.Empty;
        };
    }

    private void CreateProfileButton_OnClick(object sender, RoutedEventArgs e) =>
        ViewModel.CreateProfile();

    private void DeleteProfileButton_OnClick(object sender, RoutedEventArgs e)
    {
        var profile = ViewModel.SelectedProfile;
        if (profile == null)
            return;

        if (CustomMessageBox.Show
            (
                $"确定删除代理配置“{profile.DisplayName}”吗？",
                "代理配置",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                parentWindow: this
            ) != MessageBoxResult.Yes)
            return;

        ViewModel.DeleteSelectedProfile();
    }

    private void ConfirmButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            ViewModel.Save(ProxyPasswordBox.Password);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存代理配置失败");
            CustomMessageBox.Show
            (
                $"保存代理配置失败：{ex.Message}",
                "代理配置",
                MessageBoxButton.OK,
                MessageBoxImage.Warning,
                parentWindow: this
            );
        }
    }
}
