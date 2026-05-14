namespace XIVLauncher.Common.Constant;

public static class SdoInfos
{
    public const string APP_ID = "100001900";

    public const string CDN_KEY = "EKUWRI5KXXAIDlQ0mBNLa7XkjU1JNFuL";

    public const string GLOBAL_CAS_DOMAIN = "cas.sdo.com";

    public const string FALLBACK_CAS_DOMAIN = "n1.cas.sdo.com";

    public const string BRANCH_ID = "8847";

    public const string DOWNLOAD_CONFIG_BASE_URL = "https://ff.autopatch.sdo.com/v3launcher";

    public const string CONTENT_CONFIG_BASE_URL = "https://v3launcher.jijiagames.com/v3launcher";

    public const string VERSION_MAPPING_URL = $"{DOWNLOAD_CONFIG_BASE_URL}/mapping/v2v3Check.json";

    public const string REMOTE_VERSION_URL = $"{CONTENT_CONFIG_BASE_URL}/build/ver2data/{APP_ID}/{BRANCH_ID}/-1/ver2.dat";

    public const string CLIENT_ALL_FILES_LIST_URL = $"{CONTENT_CONFIG_BASE_URL}/build/{APP_ID}/{BRANCH_ID}/client-all-files-list/client_all_files_list.dat";
}
