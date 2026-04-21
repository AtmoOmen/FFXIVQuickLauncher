using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XIVLauncher.Common;

namespace XIVLauncher.PatchInstaller.Commands.Internal;

internal sealed class V3PatchIndexMetadataClient : IDisposable
{
    private const string BASE_URL = "https://gh.atmoomen.top/https://raw.githubusercontent.com/Dalamud-DailyRoutines/XLCNSoilAssets/master/patchInfo/";

    private readonly HttpClient client = new();

    public void Dispose() =>
        client.Dispose();

    public async Task<Dictionary<Repository, (string Version, int Revision)>> DownloadLatestVersionsAsync(CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(BASE_URL + "latest.json", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document     = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;

        return new()
        {
            [Repository.Ffxiv] = (ReadRequiredString(root, "game"), ReadRevision(root, "gameRevision")),
            [Repository.Ex1]   = (ReadRequiredString(root, "ex1"),  ReadRevision(root, "ex1Revision")),
            [Repository.Ex2]   = (ReadRequiredString(root, "ex2"),  ReadRevision(root, "ex2Revision")),
            [Repository.Ex3]   = (ReadRequiredString(root, "ex3"),  ReadRevision(root, "ex3Revision")),
            [Repository.Ex4]   = (ReadRequiredString(root, "ex4"),  ReadRevision(root, "ex4Revision")),
            [Repository.Ex5]   = (ReadRequiredString(root, "ex5"),  ReadRevision(root, "ex5Revision"))
        };
    }

    public async Task<FileInfo> DownloadPatchIndexAsync
    (
        DirectoryInfo      outputRoot,
        Repository         repository,
        string             version,
        int                revision,
        CancellationToken  cancellationToken
    )
    {
        var repoFolderName = repository == Repository.Ffxiv ? "game" : repository.ToString().ToLowerInvariant();
        var fileName       = $"{version}.patch.index";
        var targetDir      = new DirectoryInfo(Path.Combine(outputRoot.FullName, "patchMeta", repoFolderName));
        var targetPath     = Path.Combine(targetDir.FullName, fileName) + (revision > 0 ? $".v{revision}" : string.Empty);
        var targetFile     = new FileInfo(targetPath);

        if (targetFile.Exists)
            return targetFile;

        targetDir.Create();

        using var response = await client.GetAsync($"{BASE_URL}{repoFolderName}/{fileName}", HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new FileNotFoundException($"Patch index not found: {repository}/{version}", targetPath);

        response.EnsureSuccessStatusCode();

        var tempFile = new FileInfo(targetPath + ".tmp");
        await using var sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (var targetStream = tempFile.Open(FileMode.Create, FileAccess.Write, FileShare.None))
            await sourceStream.CopyToAsync(targetStream, cancellationToken).ConfigureAwait(false);

        tempFile.MoveTo(targetPath);
        return targetFile;
    }

    private static string ReadRequiredString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var property)
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static int ReadRevision(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var property) ? property.GetInt32() : 0;
}
