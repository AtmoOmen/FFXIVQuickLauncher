namespace XIVLauncher.Common.Constant;

public static class Links
{
    #region 杂项

    /// <remarks>GitHub 反代</remarks>
    public const string GITHUB_PROXY_BASE_URL = "https://gh.atmoomen.top/";
    
    /// <remarks>判断网络出口 (Cloudflare)</remarks>
    public const string CLOUDFLARE_TRACE_URL = "https://www.cloudflare.com/cdn-cgi/trace";
    
    /// <remarks>判断网络出口 (IPIP)</remarks>
    public const string IPIP_LOCATION_URL = "https://myip.ipip.net/json";

    #endregion

    
    #region 软件官网

    /// <remarks>GitHub 仓库页面</remarks>
    public const string REPO_URL = "https://github.com/AtmoOmen/FFXIVQuickLauncher";

    /// <remarks>Discord 服务器</remarks>
    public const string DISCORD_URL = "https://discord.gg/MDvv8Ejntw";

    #endregion
    

    #region 启动器

    /// <remarks>启动器 (Cloudflare R2)</remarks>
    public const string LAUNCHER_DISTRIBUTE_BASE_URL = "https://xl-dis.atmoomen.top";

    /// <remarks>启动器 (腾讯云 CNB)</remarks>
    public const string LAUNCHER_DISTRIBUTE_CNB_BASE_URL = "https://cnb.cool/atmoomen/xivlauncher-distribute/-/git/raw/master";
    
    #endregion

    
    #region Dalamud

    /// <remarks>Dalamud (Cloudflare R2)</remarks>
    public const string DALAMUD_DISTRIBUTE_R2_BASE_URL = "https://dalamud-dis.atmoomen.top";

    /// <remarks>Dalamud 版本 (Cloudflare R2)</remarks>
    public const string DALAMUD_DISTRIBUTE_R2_VERSION_URL = $"{DALAMUD_DISTRIBUTE_R2_BASE_URL}/RELEASE";

    /// <remarks>Dalamud 下载 (腾讯 CNB)</remarks>
    public const string DALAMUD_DISTRIBUTE_CNB_RELEASE_BASE_URL = "https://cnb.cool/atmoomen/dalamud-distribute/-/releases/download";

    /// <remarks>Dalamud 版本 (腾讯 CNB)</remarks>
    public const string DALAMUD_DISTRIBUTE_CNB_VERSION_URL = "https://cnb.cool/atmoomen/dalamud-distribute/-/git/raw/master/RELEASE";

    #endregion

    
    #region Dalamud 资源

    /// <remarks>资源 (Cloudflare R2)</remarks>
    public const string DALAMUD_ASSET_DISTRIBUTE_R2_BASE_URL = $"{DALAMUD_DISTRIBUTE_R2_BASE_URL}/assets";
    
    /// <remarks>资源版本 (Cloudflare R2)</remarks>
    public const string DALAMUD_ASSET_DISTRIBUTE_R2_VERSION_URL = $"{DALAMUD_ASSET_DISTRIBUTE_R2_BASE_URL}/RELEASE";

    /// <remarks>资源下载 (腾讯 CNB)</remarks>
    public const string DALAMUD_ASSET_DISTRIBUTE_CNB_RELEASE_BASE_URL = "https://cnb.cool/atmoomen/dalamud-asset-distribute/-/releases/download";

    /// <remarks>资源版本 (腾讯 CNB)</remarks>
    public const string DALAMUD_ASSET_DISTRIBUTE_CNB_VERSION_URL = "https://cnb.cool/atmoomen/dalamud-asset-distribute/-/git/raw/master/RELEASE";

    #endregion
    
    
    #region 运行时环境

    /// <remarks>硬编码的运行时版本</remarks>
    public const string DALAMUD_RUNTIME_INFO_URL = $"{GITHUB_PROXY_BASE_URL}raw.githubusercontent.com/Dalamud-DailyRoutines/XLCNSoilAssets/master/runtimeInfo";
    
    /// <remarks>微软的 NuGet 源</remarks>
    public const string NUGET_V3_FLAT_CONTAINER_URL = "https://api.nuget.org/v3-flatcontainer";

    /// <remarks>华为的 NuGet 镜像源</remarks>
    public const string HUAWEI_NUGET_V3_REMOTE_URL = "https://repo.huaweicloud.com/artifactory/api/nuget/v3/nuget-remote";

    #endregion
    
    
    #region 登录 API

    /// <remarks>请求头</remarks>
    public const string SDO_LAUNCHER_REFERER_URL = "https://ff.web.sdo.com/project/launcher0904/index.html";

    /// <remarks>大区列表</remarks>
    public const string SDO_LOGIN_AREA_URL = "https://ff.dorado.sdo.com/ff/area/serverlist_new.js";

    /// <remarks>总的服务地址</remarks>
    public const string SDO_SERVICE_URL = "http://www.sdo.com";

    #endregion
    
    
    #region 新闻 API

    /// <remarks>文章正文</remarks>
    public const string SDO_NEWS_ARTICLE_BASE_URL = "https://ff.web.sdo.com/web8/index.html#/newstab/newscont/";

    /// <remarks>轮播图</remarks>
    public const string SDO_NEWS_BANNER_API_URL = "https://cqnews.web.sdo.com/api/news/newsList?gameCode=ff&CategoryCode=5203&pageIndex=0&pageSize=8";

    /// <remarks>文章列表</remarks>
    public const string SDO_NEWS_LIST_API_URL = "https://cqnews.web.sdo.com/api/news/newsList?gameCode=ff&CategoryCode=8324,8325,8326,8327,5309,5310,5311,5312,5313&pageIndex=0&pageSize=16";

    #endregion
    
    
    #region 盛趣官方网站

    /// <remarks>超域旅行官网</remarks>
    public const string DC_TRAVEL_PAGE_URL = "https://ff14bjz.sdo.com/RegionKanTelepo";

    /// <remarks>充值官网</remarks>
    public const string SDO_PAYMENT_URL = $"https://pay.sdo.com/item/GWPAY-{SdoInfos.APP_ID}/";

    /// <remarks>商城官网</remarks>
    public const string SDO_SHOPPING_URL = "https://qu.sdo.com/game/1";

    /// <remarks>石之家社区</remarks>
    public const string RISING_STONE_URL = "https://ff14risingstones.web.sdo.com/pc/#/post";

    /// <remarks>官方哔哩哔哩账号</remarks>
    public const string SDO_BILIBILI_URL = "https://space.bilibili.com/6655514";

    /// <remarks>官方小红书账号</remarks>
    public const string SDO_XIAOHONGSHU_URL = "https://www.xiaohongshu.com/user/profile/5f814cbe0000000001003455";

    /// <remarks>官方微博账号</remarks>
    public const string SDO_WEIBO_URL = "https://weibo.com/u/1797798792";

    /// <remarks>官方抖音账号</remarks>
    public const string SDO_DOUYIN_URL = "https://www.douyin.com/user/MS4wLjABAAAAHJts6kVkO7Lob9_H5VMSc3UZXCSq6gw5s02kplXQ7k0";

    #endregion
}
