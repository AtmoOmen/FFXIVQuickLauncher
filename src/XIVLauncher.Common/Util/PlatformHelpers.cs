using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Serilog;
using SharpCompress.Archives;
using SharpCompress.Common;
using XIVLauncher.Common.Constant;

namespace XIVLauncher.Common.Util;

public static class PlatformHelpers
{
    private static readonly IPEndPoint DefaultLoopbackEndpoint = new(IPAddress.Loopback, 0);

    /// <summary>
    ///     Generates a temporary file name.
    /// </summary>
    /// <returns>A temporary file name that is almost guaranteed to be unique.</returns>
    public static string GetTempFileName()
    {
        // https://stackoverflow.com/a/50413126
        return Path.Combine(Path.GetTempPath(), "xivlauncher_" + Guid.NewGuid());
    }

    public static void DeleteAndRecreateDirectory(DirectoryInfo dir)
    {
        if (!dir.Exists)
            dir.Create();
        else
        {
            dir.Delete(true);
            dir.Create();
        }
    }

    public static void CopyFilesRecursively(DirectoryInfo source, DirectoryInfo target)
    {
        foreach (var dir in source.GetDirectories())
            CopyFilesRecursively(dir, target.CreateSubdirectory(dir.Name));

        foreach (var file in source.GetFiles())
            file.CopyTo(Path.Combine(target.FullName, file.Name));
    }

    public static bool IsElevated() =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public static void Unzip7ZAsset(string path, string output)
    {
        Log.Information("[DUPDATE] 正在解压 7z 包...");

        try
        {
            var unzipPath = Path.Combine(Paths.ResourcesPath, "7zr.exe");

            var psi = new ProcessStartInfo
            {
                FileName        = unzipPath,
                Arguments       = $"x -y -bso0 -bsp0 -o\"{output}\" \"{path}\"",
                UseShellExecute = false,
                CreateNoWindow  = true
            };

            var proc = Process.Start(psi);

            if (proc != null)
            {
                proc.WaitForExit();

                if (proc.ExitCode == 0)
                {
                    Log.Information("[DUPDATE] 7z 解压完成。");
                    return;
                }

                Log.Warning("[DUPDATE] 系统 7z 解压失败，退出码 {ExitCode}，回退到托管解压。", proc.ExitCode);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[DUPDATE] 系统 7z 不可用，回退到托管解压。");
        }

        using (var archive = ArchiveFactory.Open(path))
            archive.WriteToDirectory(output, new ExtractionOptions { ExtractFullPath = true, Overwrite = true });
        Log.Information("[DUPDATE] 托管解压完成。");
    }

    public static int GetAvailablePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        socket.Bind(DefaultLoopbackEndpoint);
        return ((IPEndPoint)socket.LocalEndPoint).Port;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetDiskFreeSpaceEx
    (
        string    lpDirectoryName,
        out ulong lpFreeBytesAvailable,
        out ulong lpTotalNumberOfBytes,
        out ulong lpTotalNumberOfFreeBytes
    );

    public static long GetDiskFreeSpace(DirectoryInfo info)
    {
        if (info == null)
            throw new ArgumentNullException(nameof(info));

        ulong dummy = 0;

        if (!GetDiskFreeSpaceEx(info.Root.FullName, out var freeSpace, out dummy, out dummy))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        return (long)freeSpace;
    }

    public static string GetVersion()
    {
        var assembly  = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var attribute = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(assembly, typeof(AssemblyInformationalVersionAttribute));
        var name      = (AssemblyProductAttribute)Attribute.GetCustomAttribute(assembly,              typeof(AssemblyProductAttribute));
        Console.WriteLine(name?.Product + " v" + attribute?.InformationalVersion);
        return name?.Product + " v" + attribute?.InformationalVersion;
    }
}
