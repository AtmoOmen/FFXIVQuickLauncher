namespace XIVLauncher.Common.Dalamud;

public class DalamudIntegrityException
(
    string     msg,
    Exception? inner = null
) : Exception(msg, inner);
