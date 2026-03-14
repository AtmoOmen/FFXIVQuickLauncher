using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Serilog;
using XIVLauncher.Common.Util;

namespace XIVLauncher.Common.Encryption;

public sealed class ArgumentBuilder
{
    private static readonly uint version = 3;

    private static readonly char[] checksumTable =
    {
        'f', 'X', '1', 'p', 'G', 't', 'd', 'S',
        '5', 'C', 'A', 'P', '4', '_', 'V', 'L'
    };

    private readonly List<KeyValuePair<string, string>> arguments;

    public ArgumentBuilder() =>
        arguments = new List<KeyValuePair<string, string>>();

    public ArgumentBuilder(IEnumerable<KeyValuePair<string, string>> items) =>
        arguments = new List<KeyValuePair<string, string>>(items);

    public ArgumentBuilder Append(string key, string value) =>
        Append(new KeyValuePair<string, string>(key, value));

    public ArgumentBuilder Append(KeyValuePair<string, string> item)
    {
        arguments.Add(item);

        return this;
    }

    public ArgumentBuilder Append(IEnumerable<KeyValuePair<string, string>> items)
    {
        arguments.AddRange(items);

        return this;
    }

    public string Build()
    {
        return arguments.Aggregate
                        (
                            new StringBuilder(),
                            (whole, part) => whole.Append($" {part.Key}={part.Value}")
                        )
                        .ToString();
    }

    public string BuildEncrypted(uint key)
    {
        var arguments = this.arguments.Aggregate
                            (
                                new StringBuilder(),
                                // Yes, they do have a space prepended even for the first argument.
                                (whole, part) => whole.Append($" /{EscapeValue(part.Key)} ={EscapeValue(part.Value)}")
                            )
                            .ToString();

        var blowfish   = new LegacyBlowfish(GetKeyBytes(key));
        var ciphertext = blowfish.Encrypt(Encoding.UTF8.GetBytes(arguments));
        var base64Str  = GameHelpers.ToMangledSeBase64(ciphertext);
        var checksum   = DeriveChecksum(key);

        Log.Information("ArgumentBuilder::BuildEncrypted() checksum:{0}", checksum);

        return $"//**sqex{version:D04}{base64Str}{checksum}**//";
    }

    public string BuildEncrypted()
    {
        var key = DeriveKey();

        return BuildEncrypted(key);
    }

    private static char DeriveChecksum(uint key)
    {
        var index = (key & 0x000F_0000) >> 16;

        try
        {
            return checksumTable[index];
        }
        catch (IndexOutOfRangeException)
        {
            return '!'; // Conceivably, this shouldn't happen...
        }
    }

    private static byte[] GetKeyBytes(uint key)
    {
        var format = $"{key:x08}";

        return Encoding.UTF8.GetBytes(format);
    }

    private static string EscapeValue(string input) =>
        input.Replace(" ", "  ");

    private uint DeriveKey()
    {
        var rawTickCount = (uint)Environment.TickCount;

        var ticks = rawTickCount & 0xFFFF_FFFFu;
        var key   = ticks        & 0xFFFF_0000u;

        Log.Information("ArgumentBuilder::DeriveKey() rawTickCount:{0} ticks:{1} key:{2}", rawTickCount, ticks, key);

        var keyPair = new KeyValuePair<string, string>("T", Convert.ToString(ticks));
        if (arguments.Count > 0 && arguments[0].Key == "T")
            arguments[0] = keyPair;
        else
            arguments.Insert(0, keyPair);

        return key;
    }
}
