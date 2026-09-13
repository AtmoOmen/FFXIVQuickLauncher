using System.IO;
using System.Linq;
using XIVLauncher.Common.Constant;
using XIVLauncher.Settings;
using XIVLauncher.Windows.ViewModel;
using Xunit;

namespace XIVLauncher.Test.Settings;

public sealed class ProxySettingsWindowViewModelTests
{
    private static string CreateTempRoamingPath() =>
        Path.Combine(Path.GetTempPath(), $"xltest-roaming-{Guid.NewGuid():N}");

    [Fact]
    public void Save_AfterCreateProfile_PersistsNewProfile()
    {
        var originalRoamingPath = Paths.RoamingPath;
        var tempRoamingPath     = CreateTempRoamingPath();

        try
        {
            Paths.OverrideRoamingPath(tempRoamingPath);

            var viewModel = new ProxySettingsWindowViewModel(new ProxySettings());
            viewModel.CreateProfile();

            var profile = viewModel.SelectedProfile;
            Assert.NotNull(profile);
            profile.Name      = "测试预设";
            profile.ProxyType = ProxyType.Http;
            profile.ProxyHost = "127.0.0.1";
            profile.ProxyPort = 8080;

            viewModel.Save(null);

            var loaded = ProxySettingsStore.Load(Paths.GetProxyConfigPath());

            var savedProfile = Assert.Single(loaded.Profiles);
            Assert.Equal(profile.Id, loaded.ActiveProfileId);
            Assert.Equal("测试预设", savedProfile.Name);
            Assert.Equal(ProxyType.Http, savedProfile.ProxyType);
            Assert.Equal("127.0.0.1", savedProfile.ProxyHost);
            Assert.Equal(8080, savedProfile.ProxyPort);
        }
        finally
        {
            Paths.OverrideRoamingPath(originalRoamingPath);
            if (Directory.Exists(tempRoamingPath))
                Directory.Delete(tempRoamingPath, true);
        }
    }

    [Fact]
    public void Save_AfterDeleteProfile_RemovesProfile()
    {
        var originalRoamingPath = Paths.RoamingPath;
        var tempRoamingPath     = CreateTempRoamingPath();

        try
        {
            Paths.OverrideRoamingPath(tempRoamingPath);

            var profile = new ProxyProfile
            {
                Name      = "待删除",
                ProxyType = ProxyType.Socks5,
                ProxyHost = "127.0.0.1",
                ProxyPort = 1080
            };

            var settings  = new ProxySettings { Profiles = [profile], ActiveProfileId = profile.Id };
            var viewModel = new ProxySettingsWindowViewModel(settings);

            Assert.NotNull(viewModel.SelectedProfile);
            viewModel.DeleteSelectedProfile();
            viewModel.Save(null);

            var loaded = ProxySettingsStore.Load(Paths.GetProxyConfigPath());

            Assert.Empty(loaded.Profiles);
            Assert.Null(loaded.ActiveProfileId);
        }
        finally
        {
            Paths.OverrideRoamingPath(originalRoamingPath);
            if (Directory.Exists(tempRoamingPath))
                Directory.Delete(tempRoamingPath, true);
        }
    }
}
