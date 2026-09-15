using System.IO;
using System.Linq;
using System.Text.Json;
using XIVLauncher.Common.Constant;
using XIVLauncher.Settings;
using Xunit;

namespace XIVLauncher.Test.Settings;

public sealed class ProxySettingsMigrationTests
{
    private static string CreateTempRoamingPath() =>
        Path.Combine(Path.GetTempPath(), $"xltest-roaming-{Guid.NewGuid():N}");

    [Fact]
    public void ImportLegacy_RoamingFile_ImportsAndDeletesFile()
    {
        var originalRoamingPath = Paths.RoamingPath;
        var tempRoamingPath     = CreateTempRoamingPath();

        try
        {
            Paths.OverrideRoamingPath(tempRoamingPath);
            Directory.CreateDirectory(tempRoamingPath);

            var profile = new ProxyProfile { Name = "旧配置", ProxyType = ProxyType.Http, ProxyHost = "127.0.0.1", ProxyPort = 8080 };
            var legacy  = new ProxySettings { Profiles = [profile], ActiveProfileId = profile.Id };
            File.WriteAllText(Paths.GetProxyConfigPath(), JsonSerializer.Serialize(legacy));

            var settings = LauncherSettingsV3.Load(Paths.GetConfigPath());

            ProxySettingsMigration.ImportLegacyInto(settings);

            var imported = Assert.Single(settings.ProxySettings.Profiles);
            Assert.Equal(profile.Id, settings.ProxySettings.ActiveProfileId);
            Assert.Equal("旧配置", imported.Name);
            Assert.Equal(ProxyType.Http, imported.ProxyType);
            Assert.Equal("127.0.0.1", imported.ProxyHost);
            Assert.Equal(8080, imported.ProxyPort);
            Assert.False(File.Exists(Paths.GetProxyConfigPath()));
        }
        finally
        {
            Paths.OverrideRoamingPath(originalRoamingPath);
            if (Directory.Exists(tempRoamingPath))
                Directory.Delete(tempRoamingPath, true);
        }
    }

    [Fact]
    public void ImportLegacy_FlatFormat_MigratesToSingleProfile()
    {
        var originalRoamingPath = Paths.RoamingPath;
        var tempRoamingPath     = CreateTempRoamingPath();

        try
        {
            Paths.OverrideRoamingPath(tempRoamingPath);
            Directory.CreateDirectory(tempRoamingPath);

            File.WriteAllText
            (
                Paths.GetProxyConfigPath(),
                """
                {
                  "ProxyType": 3,
                  "ProxyHost": "127.0.0.1",
                  "ProxyPort": 1080,
                  "ProxyUsername": "user",
                  "ProxyPasswordEncrypted": ""
                }
                """
            );

            var settings = LauncherSettingsV3.Load(Paths.GetConfigPath());

            ProxySettingsMigration.ImportLegacyInto(settings);

            var profile = Assert.Single(settings.ProxySettings.Profiles);
            Assert.Equal(ProxyType.Socks5, profile.ProxyType);
            Assert.Equal("127.0.0.1", profile.ProxyHost);
            Assert.Equal(1080, profile.ProxyPort);
            Assert.Equal(profile.Id, settings.ProxySettings.ActiveProfileId);
            Assert.False(File.Exists(Paths.GetProxyConfigPath()));
        }
        finally
        {
            Paths.OverrideRoamingPath(originalRoamingPath);
            if (Directory.Exists(tempRoamingPath))
                Directory.Delete(tempRoamingPath, true);
        }
    }

    [Fact]
    public void ImportLegacy_NoLegacyFile_DoesNothing()
    {
        var originalRoamingPath = Paths.RoamingPath;
        var tempRoamingPath     = CreateTempRoamingPath();

        try
        {
            Paths.OverrideRoamingPath(tempRoamingPath);
            Directory.CreateDirectory(tempRoamingPath);

            var settings = LauncherSettingsV3.Load(Paths.GetConfigPath());

            ProxySettingsMigration.ImportLegacyInto(settings);

            Assert.Empty(settings.ProxySettings.Profiles);
            Assert.Null(settings.ProxySettings.ActiveProfileId);
        }
        finally
        {
            Paths.OverrideRoamingPath(originalRoamingPath);
            if (Directory.Exists(tempRoamingPath))
                Directory.Delete(tempRoamingPath, true);
        }
    }

    [Fact]
    public void ImportLegacy_ExistingProxySettings_KeepsCurrentAndLegacyFile()
    {
        var originalRoamingPath = Paths.RoamingPath;
        var tempRoamingPath     = CreateTempRoamingPath();

        try
        {
            Paths.OverrideRoamingPath(tempRoamingPath);
            Directory.CreateDirectory(tempRoamingPath);

            var legacyProfile = new ProxyProfile { Name = "旧配置", ProxyType = ProxyType.Http, ProxyHost = "127.0.0.1", ProxyPort = 8080 };
            File.WriteAllText(Paths.GetProxyConfigPath(), JsonSerializer.Serialize(new ProxySettings { Profiles = [legacyProfile], ActiveProfileId = legacyProfile.Id }));

            var currentProfile = new ProxyProfile { Name = "当前配置", ProxyType = ProxyType.Socks5, ProxyHost = "127.0.0.1", ProxyPort = 1080 };
            var settings       = LauncherSettingsV3.Load(Paths.GetConfigPath());
            settings.ProxySettings = new ProxySettings { Profiles = [currentProfile], ActiveProfileId = currentProfile.Id };

            ProxySettingsMigration.ImportLegacyInto(settings);

            var keptProfile = Assert.Single(settings.ProxySettings.Profiles);
            Assert.Equal(currentProfile.Id, settings.ProxySettings.ActiveProfileId);
            Assert.Equal("当前配置", keptProfile.Name);
            Assert.True(File.Exists(Paths.GetProxyConfigPath()));
        }
        finally
        {
            Paths.OverrideRoamingPath(originalRoamingPath);
            if (Directory.Exists(tempRoamingPath))
                Directory.Delete(tempRoamingPath, true);
        }
    }

    [Fact]
    public void ImportLegacy_CorruptFile_Isolates()
    {
        var originalRoamingPath = Paths.RoamingPath;
        var tempRoamingPath     = CreateTempRoamingPath();

        try
        {
            Paths.OverrideRoamingPath(tempRoamingPath);
            Directory.CreateDirectory(tempRoamingPath);

            var legacyPath = Paths.GetProxyConfigPath();
            File.WriteAllText(legacyPath, "{ not valid json !!");

            var settings = LauncherSettingsV3.Load(Paths.GetConfigPath());

            ProxySettingsMigration.ImportLegacyInto(settings);

            Assert.Empty(settings.ProxySettings.Profiles);
            Assert.False(File.Exists(legacyPath));
            Assert.Contains(Directory.GetFiles(tempRoamingPath, Path.GetFileName(legacyPath) + ".broken-*"), file => true);
        }
        finally
        {
            Paths.OverrideRoamingPath(originalRoamingPath);
            if (Directory.Exists(tempRoamingPath))
                Directory.Delete(tempRoamingPath, true);
        }
    }
}
