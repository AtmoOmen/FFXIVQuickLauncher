namespace XIVLauncher.Common;

[AttributeUsage(AttributeTargets.Field)]
public class SettingsDescriptionAttribute : Attribute
{
    public string FriendlyName { get; set; }

    public string Description { get; set; }

    public SettingsDescriptionAttribute(string friendlyName, string description)
    {
        FriendlyName = friendlyName;
        Description  = description;
    }
}
