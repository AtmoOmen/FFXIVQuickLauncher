using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32;
using Serilog;
using Steamworks;
using XIVLauncher.Common.Game.Exceptions;
using XIVLauncher.Common.PlatformAbstractions;

namespace XIVLauncher.Common.Windows;

public class WindowsSteam : ISteam
{
    public bool IsValid => SteamClient.IsValid;

    public bool BLoggedOn => SteamClient.IsLoggedOn;

    public bool BOverlayNeedsPresent => SteamUtils.DoesOverlayNeedPresent;

    public        Task? AsyncStartTask { get; private set; }
    private const int   MAX_INIT_TRIES_AFTER_START = 15;

    public WindowsSteam() =>
        SteamUtils.OnGamepadTextInputDismissed += b => OnGamepadTextInputDismissed?.Invoke(b);

    public void KickoffAsyncStartup(uint appid) =>
        AsyncStartTask = StartAndInitialize(appid);

    public void Initialize(uint appId)
    {
        // workaround because SetEnvironmentVariable doesn't actually touch the process environment on unix
        if (Environment.OSVersion.Platform == PlatformID.Unix)
        {
            [DllImport("c")]
            static extern int setenv(string name, string value, int overwrite);

            setenv("SteamAppId", appId.ToString(), 1);
        }

        SteamClient.Init(appId);
    }

    public void Shutdown() =>
        SteamClient.Shutdown();

    public async Task<byte[]?> GetAuthSessionTicketAsync()
    {
        var ticket = await SteamUser.GetAuthSessionTicketAsync().ConfigureAwait(true);
        return ticket?.Data;
    }

    public bool IsAppInstalled(uint appId) =>
        SteamApps.IsAppInstalled(appId);

    public string GetAppInstallDir(uint appId) =>
        SteamApps.AppInstallDir(appId);

    public bool ShowGamepadTextInput(bool password, bool multiline, string description, int maxChars, string existingText = "") =>
        SteamUtils.ShowGamepadTextInput
        (
            password ? GamepadTextInputMode.Password : GamepadTextInputMode.Normal,
            multiline ? GamepadTextInputLineMode.MultipleLines : GamepadTextInputLineMode.SingleLine,
            description,
            maxChars,
            existingText
        );

    public string GetEnteredGamepadText() =>
        SteamUtils.GetEnteredGamepadText();

    public bool ShowFloatingGamepadTextInput(ISteam.EFloatingGamepadTextInputMode mode, int x, int y, int width, int height)
    {
        // Facepunch.Steamworks doesn't have this...
        return false;
    }

    public bool IsRunningOnSteamDeck() => SteamUtils.IsRunningOnSteamDeck;

    public uint GetServerRealTime() => (uint)((DateTimeOffset)SteamUtils.SteamServerTime).ToUnixTimeSeconds();

    public void ActivateGameOverlayToWebPage(string url, bool modal = false) =>
        SteamFriends.OpenWebOverlay(url, modal);

    private static void StartSteam()
    {
        var path = FindSteam();

        if (path == null || !path.Exists)
            throw new SteamException($"Failed to find Steam at {path}");

        var args = "-silent";

        if (EnvironmentSettings.IsOpenSteamMinimal)
            args += " -no-browser";

        var psi = new ProcessStartInfo
        {
            FileName  = path.FullName,
            Arguments = args
        };

        try
        {
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            throw new SteamException("Steam Process.Start failed", ex);
        }
    }

    private static FileInfo? FindSteam()
    {
        var regValue = Registry.GetValue("HKEY_CLASSES_ROOT\\steam\\Shell\\Open\\Command", null, null) as string;

        if (regValue == null || !regValue.Contains("\""))
            return null;

        return new FileInfo(regValue.Substring(1, regValue.IndexOf('"', 1) - 1));
    }

    /// <summary>
    ///     Start Steam if not already running, and initialize our app.
    /// </summary>
    /// <param name="appId">The app ID to init</param>
    private async Task StartAndInitialize(uint appId)
    {
        if (!Process.GetProcessesByName("steam").Any())
            StartSteam();

        for (var i = 0; i < MAX_INIT_TRIES_AFTER_START; i++)
        {
            await Task.Delay(1000).ConfigureAwait(false);

            try
            {
                Initialize(appId);

                Log.Verbose("Steam started automatically");
                return;
            }
            catch (Exception ex)
            {
                Log.Verbose(ex, "Steam not ready yet, waiting a little longer...");
            }
        }

        throw new SteamStartupTimedOutException();
    }

    public class SteamStartupTimedOutException : Exception
    {
        public SteamStartupTimedOutException()
            : base("Could not init Steam in time")
        {
        }
    }

    public event Action<bool> OnGamepadTextInputDismissed;
}
