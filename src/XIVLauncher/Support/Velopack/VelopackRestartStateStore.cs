using System;
using System.IO;
using System.Text.Json;
using XIVLauncher.Common.Constant;

namespace XIVLauncher.Support.Velopack;

internal static class VelopackRestartStateStore
{
    private static readonly JsonSerializerOptions JsonSerializerOptions = new()
    {
        WriteIndented = false
    };

    private static string RestartStatePath =>
        Path.Combine(Paths.RoamingPath, "velopack-restart-state.json");

    public static void Save(VelopackRestartState state)
    {
        Directory.CreateDirectory(Paths.RoamingPath);
        var json = JsonSerializer.Serialize(state, JsonSerializerOptions);
        File.WriteAllText(RestartStatePath, json);
    }

    public static VelopackRestartState? Load()
    {
        if (!File.Exists(RestartStatePath))
            return null;

        var json = File.ReadAllText(RestartStatePath);
        return JsonSerializer.Deserialize<VelopackRestartState>(json, JsonSerializerOptions);
    }

    public static void Delete()
    {
        if (!File.Exists(RestartStatePath))
            return;

        File.Delete(RestartStatePath);
    }
}
