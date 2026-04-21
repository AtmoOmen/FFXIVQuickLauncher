using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Windows;
using XIVLauncher.Common;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Util;
using XIVLauncher.Windows;
using ZipArchive = System.IO.Compression.ZipArchive;

namespace XIVLauncher.Support;

public static class PackGenerator
{
    public static string SavePack() =>
        CreatePack().PackFullName;

    public static void PackAndShowMessage(Window? parentWindow = null)
    {
        try
        {
            var (packFullName, collectedLogs, skippedLogs) = CreatePack();
            var description = new StringBuilder()
                              .Append("位置: ")
                              .Append(packFullName)
                              .Append("\n大小: ")
                              .Append(APIHelper.BytesToString(new FileInfo(packFullName).Length));

            if (collectedLogs.Length > 0)
            {
                description.Append("\n\n已收集")
                           .Append('\n')
                           .Append(string.Join("\n", collectedLogs.Select(static fileName => $"- {fileName}")));
            }

            if (skippedLogs.Length > 0)
            {
                description.Append("\n\n未纳入包内")
                           .Append('\n')
                           .Append(string.Join("\n", skippedLogs.Select(static fileName => $"- {fileName}")));
            }

            var builder = CustomMessageBox.Builder
                                          .NewFrom("已生成疑难排查包")
                                          .WithCaption("打包日志")
                                          .WithDescription(description.ToString())
                                          .WithButtons(MessageBoxButton.OKCancel)
                                          .WithOkButtonText("打开位置")
                                          .WithCancelButtonText("关闭")
                                          .WithImage(MessageBoxImage.Information)
                                          .WithShowHelpLinks(false)
                                          .WithShowDiscordLink(false)
                                          .WithShowTroubleshootingPackButton(false)
                                          .WithShowNewGitHubIssue(false);

            if (parentWindow != null)
                builder.WithParentWindow(parentWindow);

            if (builder.Show() == MessageBoxResult.OK)
                OpenPackLocation(packFullName);
        }
        catch (Exception ex)
        {
            var description = new StringBuilder()
                              .Append("未能生成疑难排查包")
                              .Append("\n\n")
                              .Append(ex);

            var builder = CustomMessageBox.Builder
                                          .NewFrom("生成疑难排查包失败")
                                          .WithCaption("打包日志")
                                          .WithDescription(description.ToString())
                                          .WithImage(MessageBoxImage.Error)
                                          .WithShowHelpLinks(false)
                                          .WithShowDiscordLink(false)
                                          .WithShowTroubleshootingPackButton(false)
                                          .WithShowNewGitHubIssue(false);

            if (parentWindow != null)
                builder.WithParentWindow(parentWindow);

            builder.Show();
        }
    }

    public static void OpenPackLocation(string packFullName) =>
        Process.Start("explorer.exe", $"/select,\"{Path.GetFullPath(packFullName)}\"");

    private static (string PackFullName, string[] CollectedLogs, string[] SkippedLogs) CreatePack()
    {
        var outFile = new FileInfo(Path.Combine(Paths.RoamingPath, $"trouble-{DateTimeOffset.Now:yyyyMMddHHmmss}.tspack"));
        using var archive = ZipFile.Open(outFile.FullName, ZipArchiveMode.Create);

        using (var troubleEntry = archive.CreateEntry("trouble.json").Open())
        {
            var troubleBytes = Encoding.UTF8.GetBytes(Troubleshooting.GetTroubleshootingJson());
            troubleEntry.Write(troubleBytes, 0, troubleBytes.Length);
        }

        List<string> collectedLogs = [];
        List<string> skippedLogs   = [];

        foreach (var logFileName in new[]
                 {
                     "output.log",
                     "patcher.log",
                     "dalamud.log",
                     "dalamud.injector.log",
                     "dalamud.boot.log",
                     "aria.log",
                     "argReader.log"
                 })
        {
            AddIfAvailable(new FileInfo(Path.Combine(Paths.RoamingPath, logFileName)), archive, collectedLogs, skippedLogs);
        }

        return (outFile.FullName, [.. collectedLogs], [.. skippedLogs]);
    }

    private static void AddIfAvailable(FileInfo file, ZipArchive zip, ICollection<string> collectedLogs, ICollection<string> skippedLogs)
    {
        if (!file.Exists)
        {
            skippedLogs.Add($"{file.Name} 未找到");
            return;
        }

        try
        {
            using var stream      = file.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var       entry       = zip.CreateEntry(file.Name);
            using var entryStream = entry.Open();
            stream.CopyTo(entryStream);
            collectedLogs.Add(file.Name);
        }
        catch (Exception ex)
        {
            skippedLogs.Add($"{file.Name} 读取失败: {ex.Message}");
        }
    }
}
