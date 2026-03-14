using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace XIVLauncher.Common.Game;

//public class SdoAreas
//{
//	[JsonProperty("servers")]
//	public SdoArea[] Servers { get; set; }
//}
public class SdoArea
{
    //"Areaid":"1",
    public string Areaid { get; set; }

    //"AreaStat":1,
    public int AreaStat { get; set; }

    //"AreaOrder":4,
    public int AreaOrder { get; set; }

    //"AreaName":"陆行鸟",
    public string AreaName { get; set; }

    //"Areatype":1,
    public int Areatype { get; set; }

    //"AreaLobby":"ffxivlobby01.ff14.sdo.com",
    public string AreaLobby { get; set; }

    //"AreaGm":"ffxivgm01.ff14.sdo.com",
    public string AreaGm { get; set; }

    //"AreaPatch":"ffxivpatch01.ff14.sdo.com",
    public string AreaPatch { get; set; }

    //"AreaConfigUpload":"ffxivsdb01.ff14.sdo.com"
    public string AreaConfigUpload { get; set; }

    public static async Task<SdoArea[]> Get()
    {
        var handler = new HttpClientHandler
        {
            UseProxy    = true,
            Proxy       = WebRequest.GetSystemWebProxy(),
            Credentials = CredentialCache.DefaultCredentials
        };

        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        var request = new HttpRequestMessage(HttpMethod.Get, "https://ff.dorado.sdo.com/ff/area/serverlist_new.js");
        request.Headers.Add("Accept", "*/*");
        request.Headers.Add("Host",   "ff.dorado.sdo.com");

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync();
        var json = text.Trim();
        json = json.Substring("var servers=".Length);
        json = json.Substring(0, json.Length - 1);
        //json = $"{{\"servers\":{json}}}";
        //Console.WriteLine(json);
        return JsonConvert.DeserializeObject<SdoArea[]>(json);
        ;
    }
}
