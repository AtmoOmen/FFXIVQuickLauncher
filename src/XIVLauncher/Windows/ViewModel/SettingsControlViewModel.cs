using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;
using Newtonsoft.Json.Linq;
using XIVLauncher.Common;
using XIVLauncher.Support;

namespace XIVLauncher.Windows.ViewModel;

internal class SettingsControlViewModel : INotifyPropertyChanged
{
    /// <summary>
    ///     Gets a value indicating whether the "Run Integrity Checks" button is enabled.
    /// </summary>
    public bool IsRunIntegrityCheckPossible =>
        !string.IsNullOrEmpty(GamePath) && Directory.Exists(GamePath);

    public List<GenericCombinedData<LauncherLanguage>> LauncherLanguageList { get; } =
        LauncherLanguageStrings
            .Zip(Enum.GetValues<LauncherLanguage>())
            .Select(pair => new GenericCombinedData<LauncherLanguage> { Display = pair.First, Value = pair.Second })
            .ToList();

    public bool _launcherLanguageNoticeVisiable;

    /// <summary>
    ///     Gets or sets the path to the game folder.
    /// </summary>
    public string GamePath
    {
        get => _gamePath;
        set
        {
            _gamePath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsRunIntegrityCheckPossible));
        }
    }

    /// <summary>
    ///     Gets or sets the path to the game folder.
    /// </summary>
    public string PatchPath
    {
        get => _patchPath;
        set
        {
            _patchPath = value;
            OnPropertyChanged();
        }
    }

    public LauncherLanguage LauncherLanguage
    {
        get => _launcherLanguage;
        set
        {
            LauncherLanguageNoticeVisiable = App.Settings.LauncherLanguage != value;
            _launcherLanguage              = value;
            OnPropertyChanged();
        }
    }

    public bool LauncherLanguageNoticeVisiable
    {
        get => _launcherLanguageNoticeVisiable;
        set
        {
            _launcherLanguageNoticeVisiable = value;
            OnPropertyChanged();
        }
    }

    public string GitHubToken
    {
        get => _gitHubToken;
        set
        {
            _gitHubToken = value;
            OnPropertyChanged();
        }
    }

    private static List<string> LauncherLanguageStrings { get; } =
    [
        "简体中文",
        "繁體中文"
    ];

    private string _gamePath;
    private string _patchPath;

    private LauncherLanguage _launcherLanguage = LauncherLanguage.SimplifiedChinese;

    private string _gitHubToken = App.Settings.GitHubToken;

    public async void IdentifyToken()
    {
        try
        {
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("XIVLauncherCN");
            if (!string.IsNullOrWhiteSpace(_gitHubToken))
                httpClient.DefaultRequestHeaders.Authorization = new("Bearer", _gitHubToken);
            var     response  = await httpClient.GetAsync("https://api.github.com/rate_limit");
            var     json      = await response.Content.ReadAsStringAsync();
            dynamic rateLimit = JObject.Parse(json);

            if (!response.IsSuccessStatusCode)
            {
                CustomMessageBox.Show($"获取 GitHub API 额度失败, 请检查你的 Token 是否正确\n{rateLimit.message}", "XIVLauncherCN (Soil)", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int remaining = rateLimit.resources.core.remaining;
            int limit     = rateLimit.resources.core.limit;

            int resetTimestamp = rateLimit.resources.core.reset;
            var resetTime      = DateTimeOffset.FromUnixTimeSeconds(resetTimestamp).LocalDateTime;
            var tokenOutput    = string.IsNullOrWhiteSpace(_gitHubToken) ? "未设置 Token, 当前 IP " : "当前 Token ";
            CustomMessageBox.Show($"{tokenOutput}的可用额度: {remaining}, 总额度: {limit}, 刷新时间: {resetTime:HH:mm:ss}", "XIVLauncherCN (Soil)");
        }
        catch (Exception ex)
        {
            CustomMessageBox.Show("获取 GitHub API 额度失败\n" + ex, "XIVLauncherCN (Soil)", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public event PropertyChangedEventHandler PropertyChanged;
}
