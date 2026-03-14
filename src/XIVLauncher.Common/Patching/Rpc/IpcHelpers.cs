using System;
using System.Text;
using Newtonsoft.Json;

namespace XIVLauncher.Common.PatcherIpc;

public static class IpcHelpers
{
    public static JsonSerializerSettings JsonSettings = new()
    {
        TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Full,
        TypeNameHandling               = TypeNameHandling.All
    };

    public static string Base64Encode(string plainText)
    {
        var plainTextBytes = Encoding.UTF8.GetBytes(plainText);
        return Convert.ToBase64String(plainTextBytes);
    }

    public static string Base64Decode(string base64EncodedData)
    {
        var base64EncodedBytes = Convert.FromBase64String(base64EncodedData);
        return Encoding.UTF8.GetString(base64EncodedBytes);
    }
}
