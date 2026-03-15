using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace XIVLauncher.Common;

public static class Paths
{
    public static string RoamingPath { get; private set; }
    
    public static string ResourcesPath => 
        Path.Join(AppContext.BaseDirectory, "Resources");
    
    static Paths() =>
        RoamingPath = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncherCN");

    public static void OverrideRoamingPath(string path) =>
        RoamingPath = Environment.ExpandEnvironmentVariables(path);
}
