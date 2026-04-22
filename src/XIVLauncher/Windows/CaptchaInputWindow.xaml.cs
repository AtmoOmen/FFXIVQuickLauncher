using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using XIVLauncher.Common.Game.Login;

namespace XIVLauncher.Windows;

public partial class CaptchaInputWindow : Window
{
    private readonly CancellationTokenSource refreshCancellationTokenSource = new();

    private LoginCaptchaChallenge currentChallenge = null!;

    public string? ResultText { get; private set; }

    public CaptchaInputWindow(LoginCaptchaChallenge challenge)
    {
        InitializeComponent();
        ApplyChallenge(challenge);

        Loaded += (_, _) =>
        {
            UpdateConfirmButtonState();
            CaptchaTextBox.Focus();
        };
        CaptchaTextBox.TextChanged += (_, _) => UpdateConfirmButtonState();
    }

    protected override void OnClosed(EventArgs e)
    {
        refreshCancellationTokenSource.Cancel();
        refreshCancellationTokenSource.Dispose();
        base.OnClosed(e);
    }

    private void TopBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                Confirm();
                e.Handled = true;
                return;

            case Key.Escape:
                Cancel();
                e.Handled = true;
                return;
        }

        base.OnPreviewKeyDown(e);
    }

    private static BitmapImage? CreateBitmapImage(byte[]? imageBytes)
    {
        if (imageBytes == null || imageBytes.Length == 0)
            return null;

        using var stream = new MemoryStream(imageBytes, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption  = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private void ConfirmButton_OnClick(object sender, RoutedEventArgs e) =>
        Confirm();

    private async void RefreshButton_OnClick(object sender, RoutedEventArgs e) =>
        await RefreshCaptchaAsync();

    private void CancelButton_OnClick(object sender, RoutedEventArgs e) =>
        Cancel();

    private void ApplyChallenge(LoginCaptchaChallenge challenge)
    {
        currentChallenge        = challenge;
        Title                   = string.IsNullOrWhiteSpace(challenge.Title) ? "验证码验证" : challenge.Title;
        PromptTextBlock.Text    = challenge.Prompt;
        CaptchaImage.Source     = CreateBitmapImage(challenge.ImageBytes);
        RefreshButton.IsEnabled = challenge.RefreshAsync != null;
    }

    private async Task RefreshCaptchaAsync()
    {
        if (currentChallenge.RefreshAsync == null)
            return;

        try
        {
            RefreshButton.IsEnabled = false;
            RefreshText.Text        = "刷新中...";

            var refreshedChallenge = await currentChallenge.RefreshAsync(refreshCancellationTokenSource.Token);
            ApplyChallenge(refreshedChallenge);
            CaptchaTextBox.Focus();
            CaptchaTextBox.SelectAll();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            CustomMessageBox.Show
            (
                $"刷新验证码失败：{ex.Message}",
                "XIVLauncherCN (Soil)",
                MessageBoxButton.OK,
                MessageBoxImage.Warning,
                parentWindow: this
            );
        }
        finally
        {
            if (IsLoaded)
            {
                RefreshText.Text        = "刷新";
                RefreshButton.IsEnabled = currentChallenge.RefreshAsync != null;
            }
        }
    }

    private void Confirm()
    {
        if (!CanConfirm())
            return;

        ResultText   = CaptchaTextBox.Text.Trim();
        DialogResult = true;
    }

    private void Cancel()
    {
        ResultText = null;
        DialogResult = false;
    }

    private bool CanConfirm() =>
        !string.IsNullOrWhiteSpace(CaptchaTextBox.Text);

    private void UpdateConfirmButtonState() =>
        ConfirmButton.IsEnabled = CanConfirm();
}
