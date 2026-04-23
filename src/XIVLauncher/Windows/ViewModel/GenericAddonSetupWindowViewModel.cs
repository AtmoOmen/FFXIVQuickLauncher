using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using XIVLauncher.Common.Addon.Implementations;

namespace XIVLauncher.Windows.ViewModel;

public class GenericAddonSetupWindowViewModel : INotifyPropertyChanged
{
    public bool CanKillAfterClose => !RunAsAdmin;

    public string Path
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    public string CommandLine
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    public bool RunAsAdmin
    {
        get;
        set
        {
            if (!SetProperty(ref field, value))
                return;

            if (value)
                KillAfterClose = false;

            OnPropertyChanged(nameof(CanKillAfterClose));
        }
    }

    public bool RunOnClose
    {
        get;
        set => SetProperty(ref field, value);
    }

    public bool KillAfterClose
    {
        get;
        set => SetProperty(ref field, value);
    }

    public void Load(GenericAddon? addon)
    {
        if (addon == null)
            return;

        Path           = addon.Path;
        CommandLine    = addon.CommandLine;
        RunAsAdmin     = addon.RunAsAdmin;
        RunOnClose     = addon.RunOnClose;
        KillAfterClose = addon.KillAfterClose;
    }

    public GenericAddon? BuildResult()
    {
        if (string.IsNullOrWhiteSpace(Path))
            return null;

        return new GenericAddon
        {
            Path           = Path,
            CommandLine    = CommandLine,
            RunAsAdmin     = RunAsAdmin,
            RunOnClose     = RunOnClose,
            KillAfterClose = KillAfterClose
        };
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public event PropertyChangedEventHandler? PropertyChanged;
}
