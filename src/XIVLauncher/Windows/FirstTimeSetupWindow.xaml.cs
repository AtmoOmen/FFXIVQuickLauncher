using XIVLauncher.Windows.Services;
using XIVLauncher.Windows.ViewModel;

namespace XIVLauncher.Windows;

/// <summary>
///     Interaction logic for FirstTimeSetup.xaml
/// </summary>
public partial class FirstTimeSetup
{
    public           bool             WasCompleted { get; private set; }
    private readonly IShortcutService _shortcutService = new ShortcutService();

    private FirstTimeSetupViewModel ViewModel => (FirstTimeSetupViewModel)DataContext;

    public FirstTimeSetup()
    {
        InitializeComponent();

        DataContext = new FirstTimeSetupViewModel(new DialogService(this), _shortcutService);
        ViewModel.CloseRequested += (_, _) =>
        {
            WasCompleted = ViewModel.WasCompleted;
            Close();
        };
        ViewModel.EnsureDesktopShortcut();
    }

    public static void CreateShortcut
    (
        string  directory,
        string  shortcutName,
        string  targetPath,
        string? description  = null,
        string? iconLocation = null
    ) =>
        new ShortcutService().CreateShortcut(directory, shortcutName, targetPath, description, iconLocation);

    public static string GetShortcutTargetFile(string path) =>
        new ShortcutService().GetShortcutTargetFile(path);
}
