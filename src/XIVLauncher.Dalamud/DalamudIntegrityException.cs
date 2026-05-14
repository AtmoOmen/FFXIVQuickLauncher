namespace XIVLauncher.Dalamud;

public class DalamudIntegrityException
(
    string     msg,
    Exception? inner = null
) : Exception(msg, inner);
