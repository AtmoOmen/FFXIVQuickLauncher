namespace XIVLauncher.Accounts.Cred;

public enum CredType
{
    NoEncryption,
    WindowsCredManager,
    WindowsHello
}

public static class CredTypeExtension
{
    extension(CredType type)
    {
        public string GetDisplayName() =>
            type switch
            {
                CredType.NoEncryption       => "无加密",
                CredType.WindowsCredManager => "系统凭据管理器",
                CredType.WindowsHello       => "Windows Hello",
                _                           => type.ToString()
            };
    } 
}
