using System.Net;
using Newtonsoft.Json;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Http;

namespace XIVLauncher.Login;

public class LoginArea
{
    private static readonly HttpClient Client = XLHttpClientFactory.Create(TimeSpan.FromSeconds(30), int.MaxValue, DecompressionMethods.None);

    static LoginArea() =>
        Client.Timeout = TimeSpan.FromSeconds(30);

    [JsonProperty("Areaid")]
    public string AreaID { get; set; } = null!;

    [JsonProperty("AreaStat")]
    public int AreaStatus { get; set; }

    [JsonProperty("AreaOrder")]
    public int AreaOrder { get; set; }

    [JsonProperty("AreaName")]
    public string AreaName { get; set; } = null!;

    [JsonProperty("Areatype")]
    public int AreaType { get; set; }

    [JsonProperty("AreaLobby")]
    public string AreaLobby { get; set; } = null!;

    [JsonProperty("AreaGm")]
    public string AreaGM { get; set; } = null!;

    [JsonProperty("AreaPatch")]
    public string AreaPatch { get; set; } = null!;

    [JsonProperty("AreaConfigUpload")]
    public string AreaConfigUpload { get; set; } = null!;

    public static async Task<LoginArea[]> Get()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Links.SDO_LOGIN_AREA_URL);
        request.Headers.Add("Accept", "*/*");
        request.Headers.Add("Host",   "ff.dorado.sdo.com");

        using var response = await Client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var text = await response.Content.ReadAsStringAsync();
        var json = text.Trim();
        json = json["var servers=".Length..];
        json = json[..^1];

        return JsonConvert.DeserializeObject<LoginArea[]>(json) ?? [];
    }
}
