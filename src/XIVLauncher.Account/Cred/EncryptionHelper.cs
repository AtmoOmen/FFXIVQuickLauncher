using System;
using System.Security.Cryptography;
using System.Text;
using NSec.Cryptography;
using Serilog;
using Argon2id = Konscious.Security.Cryptography.Argon2id;

namespace XIVLauncher.Account.Cred;

public sealed class EncryptionHelper
{
    public const     int      SaltSize  = 16;
    private const    int      KeySize   = 32; // 256 bit
    private const    int      NonceSize = 32; // 256 bit
    private readonly Aegis256 aegis;
    private readonly Key      key;

    public EncryptionHelper(byte[] password, byte[] salt)
    {
        var keyBytes = DeriveKey(password, salt);
        aegis = new Aegis256();
        key   = Key.Import(aegis, keyBytes, KeyBlobFormat.RawSymmetricKey);
    }

    public static string GenerateSalt() =>
        Convert.ToBase64String(GenerateSaltBytes());

    public static byte[] GenerateSaltBytes() =>
        GetRandomBytes(SaltSize);

    public static byte[] GetRandomBytes(int count) =>
        RandomNumberGenerator.GetBytes(count);

    public static string GetRandomBase64String(int count) =>
        Convert.ToBase64String(GetRandomBytes(count));

    public static string GetRandomHexString(int count)
    {
        return BitConverter
               .ToString(RandomNumberGenerator.GetBytes(count))
               .Replace("-", "")
               .ToLower();
    }

    public static bool IsSupported()
    {
        try
        {
            _ = new Aegis256();
            return true;
        }
        catch (Exception ex)
        {
            Log.Logger.Error
            (
                "Failed to initialize NSec.Cryptography.Aegis256 (IsSupported) {0} {1}",
                ex.Message,
                ex.StackTrace
            );
            return false;
        }
    }

    public string EncryptString(string plainText) =>
        Convert.ToBase64String(EncryptBytes(Encoding.UTF8.GetBytes(plainText)));

    public byte[] EncryptStringToBytes(string plainText) =>
        EncryptBytes(Encoding.UTF8.GetBytes(plainText));

    public byte[] EncryptBytes(byte[] bytes)
    {
        var nonce     = GenerateNonce();
        var encrypted = aegis.Encrypt(key, nonce, null, bytes);

        var result = new byte[NonceSize + encrypted.Length];
        Buffer.BlockCopy(nonce,     0, result, 0,         NonceSize);
        Buffer.BlockCopy(encrypted, 0, result, NonceSize, encrypted.Length);

        return result;
    }

    public string EncryptBytesToString(byte[] allBytes) =>
        Convert.ToBase64String(EncryptBytes(allBytes));

    public string DecryptString(string encryptedText) =>
        Encoding.UTF8.GetString(DecryptBytes(Convert.FromBase64String(encryptedText)));

    public string DecryptBytesToString(byte[] allBytes) =>
        Encoding.UTF8.GetString(DecryptBytes(allBytes));

    public byte[] DecryptStringToBytes(string encryptedText) =>
        DecryptBytes(Convert.FromBase64String(encryptedText));

    public byte[] DecryptBytes(byte[] allBytes)
    {
        var nonce = new byte[NonceSize];
        Buffer.BlockCopy(allBytes, 0, nonce, 0, NonceSize);

        var encrypted = new byte[allBytes.Length - NonceSize];
        Buffer.BlockCopy(allBytes, NonceSize, encrypted, 0, encrypted.Length);

        return aegis.Decrypt(key, nonce, null, encrypted)
               ?? throw new CryptographicException("Decryption failed (null)");
    }

    private static byte[] GenerateNonce() =>
        GetRandomBytes(NonceSize);

    private static byte[] DeriveKey(byte[] pass, byte[] salt)
    {
        var argon2id = new Argon2id(pass)
        {
            DegreeOfParallelism = 1,
            Iterations          = 3,
            MemorySize          = 67108,
            Salt                = salt
        };

        return argon2id.GetBytes(KeySize);
    }
}
