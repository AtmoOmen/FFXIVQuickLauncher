using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace XIVLauncher.Common.Game.Login;

public class LoginArea
{
    [JsonProperty("Areaid")]           public string AreaID           { get; set; }
    [JsonProperty("AreaStat")]         public int    AreaStatus       { get; set; }
    [JsonProperty("AreaOrder")]        public int    AreaOrder        { get; set; }
    [JsonProperty("AreaName")]         public string AreaName         { get; set; }
    [JsonProperty("Areatype")]         public int    AreaType         { get; set; }
    [JsonProperty("AreaLobby")]        public string AreaLobby        { get; set; }
    [JsonProperty("AreaGm")]           public string AreaGM           { get; set; }
    [JsonProperty("AreaPatch")]        public string AreaPatch        { get; set; }
    [JsonProperty("AreaConfigUpload")] public string AreaConfigUpload { get; set; }

    public static async Task<LoginArea[]> Get()
    {
        var handler = new HttpClientHandler
        {
            UseProxy    = true,
            Proxy       = WebRequest.GetSystemWebProxy(),
            Credentials = CredentialCache.DefaultCredentials
        };

        using var client = new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(30);

        var request = new HttpRequestMessage(HttpMethod.Get, "https://ff.dorado.sdo.com/ff/area/serverlist_new.js");
        request.Headers.Add("Accept", "*/*");
        request.Headers.Add("Host",   "ff.dorado.sdo.com");

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        
        var text = await response.Content.ReadAsStringAsync();
        var json = text.Trim();
        json = json["var servers=".Length..];
        json = json[..^1];

        return JsonConvert.DeserializeObject<LoginArea[]>(json);
    }
}
