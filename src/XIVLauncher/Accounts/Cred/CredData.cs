using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace XIVLauncher.Accounts.Cred;

public class CredData
{
    public string PackageName          { get; set; }
    public string Account              { get; set; }
    public string PasswordProtectedKey { get; set; }
    public string LoginSalt            { get; set; }

    private static readonly JsonSerializerOptions CredDataJsonOptions = new() { IncludeFields = true, PropertyNameCaseInsensitive = true };
    
    [JsonConstructor]
    public CredData(string packageName, string account, string passwordProtectedKey, string loginSalt)
    {
        PackageName          = packageName;
        Account              = account;
        PasswordProtectedKey = passwordProtectedKey;
        LoginSalt            = loginSalt;
    }

    public CredData(string packageName, string filename)
    {
        try
        {
            if (File.Exists(filename))
            {
                var data = JsonSerializer.Deserialize<CredData>(File.ReadAllText(filename), CredDataJsonOptions);
                
                PackageName          = data!.PackageName;
                Account              = data.Account;
                PasswordProtectedKey = data.PasswordProtectedKey;
                LoginSalt            = data.LoginSalt;
                
                Log.Information("[Cred] Loaded keys from {Filename}", filename);
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Error("[Cred] Loaded keys from {Filename} failed\n{Exception}", filename, ex);
        }

        PackageName          = packageName;
        PasswordProtectedKey = EncryptionHelper.GetRandomBase64String(128);
        Account              = EncryptionHelper.GetRandomHexString(8);
        LoginSalt            = EncryptionHelper.GenerateSalt();
        
        Log.Information("[Cred] Make new keys");
        var text = JsonSerializer.Serialize(this);
        File.WriteAllText(filename, text);
        
        Log.Information("[Cred] Save keys from {Filename}", filename);
    }
}
