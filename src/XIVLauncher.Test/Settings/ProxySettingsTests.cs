using System.IO;
using System.Linq;
using XIVLauncher.Settings;
using Xunit;

namespace XIVLauncher.Test.Settings;

public sealed class ProxySettingsTests
{
    private static string CreateTempPath() =>
        Path.Combine(Path.GetTempPath(), $"xltest-settings-{Guid.NewGuid():N}.json");

    [Fact]
    public void ToSnapshot_NoActiveProfile_ReturnsNull()
    {
        var settings = new ProxySettings
        {
            Profiles =
            [
                new ProxyProfile
                {
                    Name      = "未启用",
                    ProxyType = ProxyType.Socks5,
                    ProxyHost = "127.0.0.1",
                    ProxyPort = 1080
                }
            ],
            ActiveProfileId = null
        };

        Assert.Null(settings.ToSnapshot());
    }

    [Fact]
    public void ToSnapshot_ActiveProfile_MapsFields()
    {
        var profile = new ProxyProfile
        {
            Name          = "A",
            ProxyType     = ProxyType.Socks5,
            ProxyHost     = "127.0.0.1",
            ProxyPort     = 1080,
            ProxyUsername = "user"
        };
        profile.SetPassword("pass");

        var settings = new ProxySettings
        {
            Profiles        = [profile],
            ActiveProfileId = profile.Id
        };

        var snapshot = settings.ToSnapshot();

        Assert.NotNull(snapshot);
        Assert.Equal("Socks5", snapshot.Type);
        Assert.Equal("127.0.0.1", snapshot.Host);
        Assert.Equal(1080, snapshot.Port);
        Assert.Equal("user", snapshot.Username);
        Assert.Equal("pass", snapshot.Password);
    }

    [Fact]
    public void SetPassword_Empty_ClearsPassword()
    {
        var profile = new ProxyProfile();
        profile.SetPassword("secret");

        Assert.Equal("secret", profile.GetPassword());
        Assert.NotEmpty(profile.ProxyPasswordEncrypted);

        profile.SetPassword(null);

        Assert.Null(profile.GetPassword());
        Assert.Empty(profile.ProxyPasswordEncrypted);
    }

    [Fact]
    public void ToSnapshot_InvalidPort_ReturnsNull()
    {
        var profile = new ProxyProfile { ProxyType = ProxyType.Http, ProxyHost = "127.0.0.1", ProxyPort = 70000 };

        Assert.Null(profile.ToSnapshot());
    }

    [Fact]
    public void DeepClone_ReturnsIndependentCopy()
    {
        var profile  = new ProxyProfile { Name = "A", ProxyType = ProxyType.Http, ProxyHost = "127.0.0.1", ProxyPort = 8080 };
        var settings = new ProxySettings { Profiles = [profile], ActiveProfileId = profile.Id };

        var clone = settings.DeepClone();

        Assert.NotSame(settings, clone);
        Assert.Equal(profile.Id, clone.ActiveProfileId);
        Assert.Single(clone.Profiles);

        clone.Profiles[0].ProxyHost = "10.0.0.1";
        clone.Profiles.RemoveAt(0);

        Assert.Equal("127.0.0.1", settings.Profiles[0].ProxyHost);
        Assert.Single(settings.Profiles);
    }

    [Fact]
    public void LauncherSettingsV3_SaveLoad_RoundTrip_PreservesProxySettings()
    {
        var path = CreateTempPath();

        try
        {
            var profile = new ProxyProfile
            {
                Name          = "A",
                ProxyType     = ProxyType.Socks5,
                ProxyHost     = "127.0.0.1",
                ProxyPort     = 1080,
                ProxyUsername = "user"
            };
            profile.SetPassword("secret");

            var settings = LauncherSettingsV3.Load(path);
            settings.ProxySettings = new ProxySettings { Profiles = [profile], ActiveProfileId = profile.Id };
            settings.Save();

            var reloaded = LauncherSettingsV3.Load(path);

            var loadedProfile = Assert.Single(reloaded.ProxySettings.Profiles);
            Assert.Equal(profile.Id, reloaded.ProxySettings.ActiveProfileId);
            Assert.Equal(ProxyType.Socks5, loadedProfile.ProxyType);
            Assert.Equal("127.0.0.1", loadedProfile.ProxyHost);
            Assert.Equal(1080, loadedProfile.ProxyPort);
            Assert.Equal("user", loadedProfile.ProxyUsername);
            Assert.Equal("secret", loadedProfile.GetPassword());
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".bak");
        }
    }
}
