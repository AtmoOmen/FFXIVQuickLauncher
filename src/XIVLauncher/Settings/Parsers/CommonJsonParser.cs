using System;
using System.Collections.Generic;
using Config.Net;
using Newtonsoft.Json;

namespace XIVLauncher.Settings.Parsers;

public class CommonJsonParser<T> : ITypeParser
{
    public IEnumerable<Type> SupportedTypes => new[] { typeof(T) };

    public bool TryParse(string value, Type t, out object result)
    {
        try
        {
            result = JsonConvert.DeserializeObject(value, t);
        }
        catch
        {
            result = null;
            return false;
        }

        return true;
    }

    public string ToRawString(object value) =>
        value == null ? null : JsonConvert.SerializeObject(value);
}
