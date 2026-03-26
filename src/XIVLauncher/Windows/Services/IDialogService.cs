using System.Windows;
using XIVLauncher.Accounts;
using XIVLauncher.Common.Addon.Implementations;

namespace XIVLauncher.Windows.Services;

internal interface IDialogService
{
    MessageBoxResult ShowMessage(CustomMessageBox.Builder builder);

    MessageBoxResult ShowMessage
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
    );

    string? ShowTextInput(string text, string caption, string initialText, Window? parentWindow = null);

    bool ShowFirstTimeSetup(out bool wasCompleted);

    void ShowAdvancedSettings();

    GenericAddon? ShowGenericAddonSetup(GenericAddon? addon = null);

    bool ShowProfilePictureInput(XIVAccount account, out string? profileImagePath);

    void ShowChangelog(string version);
}
