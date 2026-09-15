using XIVLauncher.Settings;
using XIVLauncher.Windows.ViewModel;
using Xunit;

namespace XIVLauncher.Test.Settings;

public sealed class ProxySettingsWindowViewModelTests
{
    [Fact]
    public void Save_AfterCreateProfile_UpdatesSettings()
    {
        var viewModel = new ProxySettingsWindowViewModel(new ProxySettings());
        viewModel.CreateProfile();

        var profile = viewModel.SelectedProfile;
        Assert.NotNull(profile);
        profile.Name      = "测试预设";
        profile.ProxyType = ProxyType.Http;
        profile.ProxyHost = "127.0.0.1";
        profile.ProxyPort = 8080;

        viewModel.Save(null);

        var savedProfile = Assert.Single(viewModel.Settings.Profiles);
        Assert.Equal(profile.Id, viewModel.Settings.ActiveProfileId);
        Assert.Equal("测试预设", savedProfile.Name);
        Assert.Equal(ProxyType.Http, savedProfile.ProxyType);
        Assert.Equal("127.0.0.1", savedProfile.ProxyHost);
        Assert.Equal(8080, savedProfile.ProxyPort);
    }

    [Fact]
    public void Save_AfterDeleteProfile_ClearsSettings()
    {
        var profile = new ProxyProfile
        {
            Name      = "待删除",
            ProxyType = ProxyType.Socks5,
            ProxyHost = "127.0.0.1",
            ProxyPort = 1080
        };

        var viewModel = new ProxySettingsWindowViewModel(new ProxySettings { Profiles = [profile], ActiveProfileId = profile.Id });

        Assert.NotNull(viewModel.SelectedProfile);
        viewModel.DeleteSelectedProfile();
        viewModel.Save(null);

        Assert.Empty(viewModel.Settings.Profiles);
        Assert.Null(viewModel.Settings.ActiveProfileId);
    }

    [Fact]
    public void Save_EmptyName_Throws()
    {
        var profile = new ProxyProfile
        {
            Name      = " ",
            ProxyType = ProxyType.Http,
            ProxyHost = "127.0.0.1",
            ProxyPort = 8080
        };

        var viewModel = new ProxySettingsWindowViewModel(new ProxySettings { Profiles = [profile], ActiveProfileId = profile.Id });

        Assert.Throws<InvalidOperationException>(() => viewModel.Save(null));
    }

    [Fact]
    public void Save_InvalidPort_Throws()
    {
        var profile = new ProxyProfile
        {
            Name      = "A",
            ProxyType = ProxyType.Http,
            ProxyHost = "127.0.0.1",
            ProxyPort = 70000
        };

        var viewModel = new ProxySettingsWindowViewModel(new ProxySettings { Profiles = [profile], ActiveProfileId = profile.Id });

        Assert.Throws<InvalidOperationException>(() => viewModel.Save(null));
    }
}
