using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;

namespace XIVLauncher.Common.Dalamud
{
    public class DalamudSettings
    {
        public const string ReleaseChannel = "release";
        public const string CanaryChannel = "canary";
        public const string StagingChannel = "staging";

        public string? DalamudBetaKey { get; set; } = null;
        public bool DoDalamudRuntime { get; set; } = false;
        public string DalamudBetaKind { get; set; } = ReleaseChannel;

        public static string GetConfigPath(DirectoryInfo configFolder) => Path.Combine(configFolder.FullName, "dalamudConfig.json");

        public static string NormalizeDalamudBetaKind(string dalamudBetaKind)
        {
            if (string.Equals(dalamudBetaKind, CanaryChannel, StringComparison.OrdinalIgnoreCase))
                return CanaryChannel;

            if (string.Equals(dalamudBetaKind, StagingChannel, StringComparison.OrdinalIgnoreCase))
                return StagingChannel;

            return ReleaseChannel;
        }

        public static bool IsCanaryChannel(string dalamudBetaKind) =>
            string.Equals(NormalizeDalamudBetaKind(dalamudBetaKind), CanaryChannel, StringComparison.OrdinalIgnoreCase);

        public static bool IsStagingChannel(string dalamudBetaKind) =>
            string.Equals(NormalizeDalamudBetaKind(dalamudBetaKind), StagingChannel, StringComparison.OrdinalIgnoreCase);

        public static DalamudSettings GetSettings(DirectoryInfo configFolder)
        {
            var configPath = GetConfigPath(configFolder);
            DalamudSettings deserialized = null;

            try
            {
                deserialized = File.Exists(configPath) ? JsonConvert.DeserializeObject<DalamudSettings>(File.ReadAllText(configPath)) : new DalamudSettings();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Couldn't deserialize Dalamud settings");
            }

            deserialized ??= new DalamudSettings(); // In case the .json is corrupted
            deserialized.DalamudBetaKind = NormalizeDalamudBetaKind(deserialized.DalamudBetaKind);
            return deserialized;
        }

        public static void SaveUpdateChannel(DirectoryInfo configFolder, string updateChannel, string stagingKey = null)
        {
            Directory.CreateDirectory(configFolder.FullName);

            var configPath = GetConfigPath(configFolder);
            var configJson = new JObject();

            try
            {
                if (File.Exists(configPath))
                    configJson = JObject.Parse(File.ReadAllText(configPath));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Couldn't deserialize Dalamud settings");
            }

            var normalizedUpdateChannel = NormalizeDalamudBetaKind(updateChannel);

            configJson[nameof(DalamudBetaKind)] = normalizedUpdateChannel == ReleaseChannel ? JValue.CreateNull() : new JValue(normalizedUpdateChannel);

            if (!string.IsNullOrWhiteSpace(stagingKey))
                configJson[nameof(DalamudBetaKey)] = new JValue(stagingKey.Trim());
            else if (IsStagingChannel(normalizedUpdateChannel))
                configJson[nameof(DalamudBetaKey)] = JValue.CreateNull();

            File.WriteAllText(configPath, configJson.ToString(Formatting.Indented));
        }
    }
}
