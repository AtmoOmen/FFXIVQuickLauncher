using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using XIVLauncher.Common.Constant;
using XIVLauncher.Settings;

namespace XIVLauncher.Windows.ViewModel;

public sealed partial class ProxySettingsWindowViewModel : ObservableObject
{
    /// <summary>
    ///     编辑会话使用的配置副本
    /// </summary>
    public ProxySettings Settings { get; }

    public sealed record ProxyTypeOption(ProxyType Type, string Display);

    public sealed record ProxySelectItem(ProxyProfile? Profile)
    {
        public string DisplayName =>
            Profile is null ? "不使用代理" : Profile.DisplayName;
    }

    public IReadOnlyList<ProxyTypeOption> ProxyTypeOptions { get; } =
    [
        new(ProxyType.None, "不使用代理"),
        new(ProxyType.Http, "HTTP"),
        new(ProxyType.Https, "HTTPS"),
        new(ProxyType.Socks5, "SOCKS5")
    ];

    public ObservableCollection<ProxyProfile>    Profiles      { get; } = [];
    public ObservableCollection<ProxySelectItem> SelectionItems { get; } = [];

    [ObservableProperty]
    public partial ProxySelectItem SelectedItem { get; set; }

    [ObservableProperty]
    public partial string ConfigFilePath { get; set; } = string.Empty;

    public ProxyProfile? SelectedProfile =>
        SelectedItem?.Profile;

    public bool HasSelectedProfile =>
        SelectedProfile != null;

    public string PasswordHint =>
        HasSelectedProfile
        && !string.IsNullOrWhiteSpace(SelectedProfile.ProxyPasswordEncrypted) ?
            "留空保持当前密码" :
            "未设置";

    public bool CanClearPassword =>
        HasSelectedProfile
        && !string.IsNullOrWhiteSpace(SelectedProfile.ProxyPasswordEncrypted);

    public ProxySettingsWindowViewModel(ProxySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Settings       = settings;
        ConfigFilePath = Paths.GetConfigPath();

        foreach (var profile in settings.Profiles)
            Profiles.Add(profile);

        RebuildSelection();

        var activeProfile = settings.GetActiveProfile();
        SelectedItem = activeProfile is null ?
                           SelectionItems[0] :
                           SelectionItems.First(item => ReferenceEquals(item.Profile, activeProfile));
    }

    partial void OnSelectedItemChanged(ProxySelectItem? value)
    {
        OnPropertyChanged(nameof(SelectedProfile));
        OnPropertyChanged(nameof(HasSelectedProfile));
        OnPropertyChanged(nameof(PasswordHint));
        OnPropertyChanged(nameof(CanClearPassword));
    }

    public void ClearPassword()
    {
        if (SelectedProfile == null)
            return;

        SelectedProfile.SetPassword(null);
        OnPropertyChanged(nameof(PasswordHint));
        OnPropertyChanged(nameof(CanClearPassword));
    }

    public void CreateProfile()
    {
        var profile = new ProxyProfile { Name = "新代理配置", ProxyPort = 1080 };
        Profiles.Add(profile);
        RebuildSelection();
        SelectedItem = SelectionItems.First(item => ReferenceEquals(item.Profile, profile));
    }

    public void DeleteSelectedProfile()
    {
        var profile = SelectedProfile;
        if (profile == null)
            return;

        Profiles.Remove(profile);
        RebuildSelection();
        SelectedItem = SelectionItems[0];
    }

    public void Save(string? passwordInput)
    {
        var profile = SelectedProfile;

        if (profile != null)
        {
            if (string.IsNullOrWhiteSpace(profile.Name))
                throw new InvalidOperationException("配置名称不能为空。");

            if (profile.ProxyType != ProxyType.None)
            {
                if (string.IsNullOrWhiteSpace(profile.ProxyHost))
                    throw new InvalidOperationException("代理主机不能为空。");

                if (profile.ProxyPort is < 1 or > 65535)
                    throw new InvalidOperationException("代理端口必须在 1 - 65535 之间。");
            }

            profile.Name         = profile.Name.Trim();
            profile.ProxyHost    = profile.ProxyHost.Trim();
            profile.ProxyUsername = profile.ProxyUsername.Trim();

            if (!string.IsNullOrEmpty(passwordInput))
                profile.SetPassword(passwordInput);
        }

        Settings.Profiles        = Profiles.ToList();
        Settings.ActiveProfileId = profile?.Id;
    }

    private void RebuildSelection()
    {
        SelectionItems.Clear();
        SelectionItems.Add(new ProxySelectItem(null));

        foreach (var profile in Profiles)
            SelectionItems.Add(new ProxySelectItem(profile));
    }
}
