namespace XIVLauncher.Common.Constant;

public static class SdoInfos
{
    // 启动器用
    public const string LAUNCHER_APP_ID = "791000814";
    
    // 原来的老启动器, 现在用做游戏登录等各种操作鉴权
    public const string APP_ID = "100001900";

    public const string CDN_KEY = "EKUWRI5KXXAIDlQ0mBNLa7XkjU1JNFuL";

    public const string GLOBAL_CAS_DOMAIN = "cas.sdo.com";

    public const string FALLBACK_CAS_DOMAIN = "n1.cas.sdo.com";

    public const string BRANCH_ID = "8847";
    
    public const string CONTENT_CONFIG_BASE_URL = "https://v3launcher.jijiagames.com/v3launcher";
    
    public const string REMOTE_VERSION_URL = $"{CONTENT_CONFIG_BASE_URL}/build/ver2data/{APP_ID}/{BRANCH_ID}/-1/ver2.dat";

    public const string CLIENT_ALL_FILES_LIST_URL = $"{CONTENT_CONFIG_BASE_URL}/build/{APP_ID}/{BRANCH_ID}/client-all-files-list/client_all_files_list.dat";
    
    public const string DEFAULT_MINIMUM_SUPPORTED_DATA_VERSION = "0.0.0.7";
}
