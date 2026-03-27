using System.Windows;
using XIVLauncher.Accounts;
using XIVLauncher.Common.Addon.Implementations;

namespace XIVLauncher.Windows.Services;

internal sealed class DialogService
(
    Window? owner = null
) : IDialogService
{
    public MessageBoxResult ShowMessage(CustomMessageBox.Builder builder) =>
        builder.Show();

    public MessageBoxResult ShowMessage
    (
        string           text,
        string           caption,
        MessageBoxButton buttons              = MessageBoxButton.OK,
        MessageBoxImage  image                = MessageBoxImage.Asterisk,
        bool             showHelpLinks        = true,
        bool             showDiscordLink      = true,
        bool             showReportLinks      = false,
        bool             showOfficialLauncher = false,
        Window?          parentWindow         = null
    ) =>
        CustomMessageBox.Show
        (
            text,
            caption,
            buttons,
            image,
            showHelpLinks,
            showDiscordLink,
            showReportLinks,
            showOfficialLauncher,
            parentWindow ?? owner!
        );

    public string? ShowTextInput(string text, string caption, string initialText, Window? parentWindow = null)
    {
        var builder = CustomMessageBox.Builder.NewFrom(text)
                                      .WithCaption(caption)
                                      .WithButtons(MessageBoxButton.OKCancel)
                                      .WithInputTextBox(initialText)
                                      .WithParentWindow(parentWindow ?? owner!);

        return builder.Show() == MessageBoxResult.OK ? builder.InputTextBoxText : null;
    }

    public bool ShowFirstTimeSetup(out bool wasCompleted)
    {
        var window = new FirstTimeSetup();
        PrepareOwner(window);
        window.ShowDialog();
        wasCompleted = window.WasCompleted;
        return wasCompleted;
    }

    public void ShowAdvancedSettings()
    {
        var window = new AdvancedSettingsWindow();
        PrepareOwner(window);
        window.ShowDialog();
    }

    public GenericAddon? ShowGenericAddonSetup(GenericAddon? addon = null)
    {
        var window = new GenericAddonSetupWindow(addon);
        PrepareOwner(window);
        window.ShowDialog();
        return window.Result;
    }

    public bool ShowProfilePictureInput(XIVAccount account, out string? profileImagePath)
    {
        var window = new ProfilePictureInputWindow(account);
        PrepareOwner(window);
        var dialogResult = window.ShowDialog();
        profileImagePath = window.ResultPath;
        return dialogResult == true;
    }

    public void ShowChangelog(string version)
    {
        var window = new ChangelogWindow();
        PrepareOwner(window);
        window.UpdateVersion(version);
        window.ShowDialog();
    }

    private void PrepareOwner(Window window)
    {
        if (owner?.IsVisible == true)
        {
            window.Owner         = owner;
            window.ShowInTaskbar = false;
        }
    }
}
