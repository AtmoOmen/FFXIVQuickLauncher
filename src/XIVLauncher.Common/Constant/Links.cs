namespace XIVLauncher.Common.Constant;

public static class Links
{
    public const string REPO_URL = "https://github.com/AtmoOmen/FFXIVQuickLauncher";

    public const string DISCORD_URL = "https://discord.gg/MDvv8Ejntw";

    public const string GITHUB_PROXY_BASE_URL = "https://gh.atmoomen.top/";

    public const string GITHUB_API_BASE_URL = "https://api.github.com/";

    public const string DALAMUD_RUNTIME_INFO_URL = $"{GITHUB_PROXY_BASE_URL}raw.githubusercontent.com/Dalamud-DailyRoutines/XLCNSoilAssets/master/runtimeInfo";

    public const string LAUNCHER_DISTRIBUTE_BASE_URL = "https://xl-dis.atmoomen.top";

    public const string LAUNCHER_DISTRIBUTE_CNB_BASE_URL = "https://cnb.cool/atmoomen/xivlauncher-distribute/-/git/raw/master";

    public const string NETWORK_ENVIRONMENT_TRACE_URL = $"{LAUNCHER_DISTRIBUTE_BASE_URL}/cdn-cgi/trace";

    public const string CLOUDFLARE_TRACE_URL = "https://www.cloudflare.com/cdn-cgi/trace";

    // Cloudflare R2 (国际)
    public const string DALAMUD_DISTRIBUTE_R2_BASE_URL = "https://dalamud-dis.atmoomen.top";

    // Cloudflare R2 (国际)
    public const string DALAMUD_DISTRIBUTE_R2_VERSION_URL = $"{DALAMUD_DISTRIBUTE_R2_BASE_URL}/RELEASE";

    // 腾讯 CNB (国内)
    // 形似 https://cnb.cool/atmoomen/dalamud-distribute/-/releases/download/26-07-29-01/latest.7z
    public const string DALAMUD_DISTRIBUTE_CNB_RELEASE_BASE_URL = "https://cnb.cool/atmoomen/dalamud-distribute/-/releases/download";

    // 腾讯 CNB (国内)
    public const string DALAMUD_DISTRIBUTE_CNB_VERSION_URL = "https://cnb.cool/atmoomen/dalamud-distribute/-/git/raw/master/RELEASE";

    public const string DALAMUD_ASSET_DISTRIBUTE_R2_BASE_URL = $"{DALAMUD_DISTRIBUTE_R2_BASE_URL}/assets";

    public const string DALAMUD_ASSET_DISTRIBUTE_CNB_RELEASE_BASE_URL = "https://cnb.cool/atmoomen/dalamud-asset-distribute/-/releases/download";

    public const string DALAMUD_ASSET_DISTRIBUTE_CNB_VERSION_URL = "https://cnb.cool/atmoomen/dalamud-asset-distribute/-/git/raw/master/RELEASE";

    public const string NUGET_V3_FLAT_CONTAINER_URL = "https://api.nuget.org/v3-flatcontainer";

    public const string HUAWEI_NUGET_V3_REMOTE_URL = "https://repo.huaweicloud.com/artifactory/api/nuget/v3/nuget-remote";

    public const string SDO_NEWS_ARTICLE_BASE_URL = "https://ff.web.sdo.com/web8/index.html#/newstab/newscont/";

    public const string SDO_NEWS_BANNER_API_URL = "https://cqnews.web.sdo.com/api/news/newsList?gameCode=ff&CategoryCode=5203&pageIndex=0&pageSize=8";

    public const string SDO_NEWS_LIST_API_URL = "https://cqnews.web.sdo.com/api/news/newsList?gameCode=ff&CategoryCode=8324,8325,8326,8327,5309,5310,5311,5312,5313&pageIndex=0&pageSize=16";

    public const string SDO_LAUNCHER_REFERER_URL = "https://ff.web.sdo.com/project/launcher0904/index.html";

    public const string SDO_LOGIN_AREA_URL = "https://ff.dorado.sdo.com/ff/area/serverlist_new.js";

    public const string SDO_SERVICE_URL = "http://www.sdo.com";

    public const string DC_TRAVEL_PAGE_URL = "https://ff14bjz.sdo.com/RegionKanTelepo";

    public const string SDO_PAYMENT_URL = $"https://pay.sdo.com/item/GWPAY-{SdoInfos.APP_ID}/";

    public const string SDO_SHOPPING_URL = "https://qu.sdo.com/game/1";

    public const string RISING_STONE_URL = "https://ff14risingstones.web.sdo.com/pc/#/post";

    public const string SDO_BILIBILI_URL = "https://space.bilibili.com/6655514";

    public const string SDO_XIAOHONGSHU_URL = "https://www.xiaohongshu.com/user/profile/5f814cbe0000000001003455";

    public const string SDO_WEIBO_URL = "https://weibo.com/u/1797798792";

    public const string SDO_DOUYIN_URL = "https://www.douyin.com/user/MS4wLjABAAAAHJts6kVkO7Lob9_H5VMSc3UZXCSq6gw5s02kplXQ7k0";
}
