using System;
using System.Windows;
using Microsoft.Win32;
using XIVLauncher.Account;
using XIVLauncher.Windows.ViewModel;

namespace XIVLauncher.Windows;

public partial class AccountProfileWindow
{
    public string? ResultPath { get; private set; }

    private AccountProfileWindowViewModel ViewModel => (AccountProfileWindowViewModel)DataContext;

    public AccountProfileWindow(XIVAccount account)
    {
        InitializeComponent();

        DataContext = new AccountProfileWindowViewModel();
        ViewModel.Load(account);
    }

    private void BrowseButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title           = "选择头像文件",
            CheckFileExists = true,
            Multiselect     = false,
            Filter          = "头像文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.ico|图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif|图标文件|*.ico|所有文件|*.*"
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            ViewModel.SetPreviewImage(dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show
            (
                this,
                $"无法加载所选头像文件。\n{ex.Message}",
                "设置头像",
                MessageBoxButton.OK,
                MessageBoxImage.Error
            );
        }
    }

    private void ClearButton_OnClick(object sender, RoutedEventArgs e) =>
        ViewModel.ClearPreviewImage();

    private void ConfirmButton_OnClick(object sender, RoutedEventArgs e)
    {
        ResultPath = ViewModel.SelectedFilePath;
        ViewModel.ApplyChanges();
        DialogResult = true;
    }
}
