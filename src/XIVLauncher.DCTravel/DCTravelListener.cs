using System.Collections.Frozen;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using Serilog;
using XIVLauncher.Common.Http;

namespace XIVLauncher.DCTravel;

public sealed class DCTravelListener : IDisposable, IAsyncDisposable
{
    public DCTravelClient DCTravelClient { get; }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly CancellationTokenSource              listenerCts = new();
    private readonly FrozenDictionary<string, MethodInfo> rpcMethodCache;
    private readonly byte[]?                              key;
    private readonly byte[]?                              iv;
    private readonly bool                                 useEncrypt;

    private WebServer? webServer;
    private int        stopState;
    private int        disposeState;

    public DCTravelListener(DCTravelClient dcTravelClient, int port, bool useEncrypt = true)
    {
        ArgumentNullException.ThrowIfNull(dcTravelClient);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);
        if (port > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(port), "port 必须在 1-65535 范围内");

        DCTravelClient  = dcTravelClient;
        this.useEncrypt = useEncrypt;
        if (useEncrypt)
            (key, iv) = GenerateAesKeyIv();
        rpcMethodCache = BuildRpcMethodCache();

        var urlPrefix = new UriBuilder(Uri.UriSchemeHttp, "127.0.0.1", port).Uri.ToString().TrimEnd('/');

        webServer = new WebServer
            (o => o
                  .WithUrlPrefix(urlPrefix)
                  .WithMode(HttpListenerMode.EmbedIO)
            )
            .WithWebApi("/dctravel", m => m.WithController(() => new RpcController(this)));
    }

    #region Disposal

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposeState, 1) != 0)
            return;

        Stop();
        listenerCts.Dispose();
    }

    #endregion

    public void Stop()
    {
        if (Interlocked.Exchange(ref stopState, 1) != 0)
            return;

        listenerCts.Cancel();
        DCTravelClient.KeepAliveCancelSource.Cancel();

        try
        {
            DCTravelClient.Logout().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[DCTravelListener] 登出失败");
        }

        Interlocked.Exchange(ref webServer, null)?.Dispose();
    }

    public async Task StartAsync()
    {
        try
        {
            ThrowIfDisposed();
            var server = webServer ?? throw new ObjectDisposedException(nameof(DCTravelListener));
            await server.RunAsync(listenerCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Log.Information("[DCTravelListener] 已取消");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DCTravelListener] 发生异常");
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    internal string Encrypt(string plainText)
    {
        ArgumentNullException.ThrowIfNull(plainText);
        if (!useEncrypt)
            return plainText;

        using var aes = Aes.Create();
        aes.Key = key ?? throw new InvalidOperationException("加密密钥未初始化");
        aes.IV  = iv  ?? throw new InvalidOperationException("加密 IV 未初始化");

        using var encryptor      = aes.CreateEncryptor();
        var       plainBytes     = Encoding.UTF8.GetBytes(plainText);
        var       encryptedBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
        return Convert.ToBase64String(encryptedBytes);
    }

    internal string Decrypt(string cipherText)
    {
        ArgumentNullException.ThrowIfNull(cipherText);
        if (!useEncrypt)
            return cipherText;

        var       buffer = Convert.FromBase64String(cipherText);
        using var aes    = Aes.Create();
        aes.Key = key ?? throw new InvalidOperationException("加密密钥未初始化");
        aes.IV  = iv  ?? throw new InvalidOperationException("加密 IV 未初始化");

        using var decryptor      = aes.CreateDecryptor();
        var       decryptedBytes = decryptor.TransformFinalBlock(buffer, 0, buffer.Length);
        return Encoding.UTF8.GetString(decryptedBytes);
    }

    private static (byte[] key, byte[] iv) GenerateAesKeyIv()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var iv  = RandomNumberGenerator.GetBytes(16);
        return (key, iv);
    }

    private static FrozenDictionary<string, MethodInfo> BuildRpcMethodCache()
    {
        var methods = typeof(DCTravelClient)
                      .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                      .Where(m => m.GetCustomAttribute<HttpRpcAttribute>() != null);

        var result = new Dictionary<string, MethodInfo>(StringComparer.Ordinal);

        foreach (var method in methods)
        {
            if (!result.TryAdd(method.Name, method))
                throw new InvalidOperationException($"重复的 RPC 方法名: {method.Name}");
        }

        return result.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static object?[] BindParameters(ParameterInfo[] parameters, object?[] callArguments)
    {
        if (parameters.Length != callArguments.Length)
            throw new InvalidOperationException("参数数量不匹配");

        if (parameters.Length == 0)
            return [];

        var result = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
            result[i] = ConvertArgument(callArguments[i], parameters[i].ParameterType);
        return result;
    }

    private static object? ConvertArgument(object? value, Type targetType)
    {
        if (value is JsonElement jsonElement)
            return jsonElement.Deserialize(targetType, SerializerOptions);

        var nonNullableType = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (value is null)
        {
            if (nonNullableType.IsValueType && Nullable.GetUnderlyingType(targetType) is null)
                throw new InvalidOperationException($"参数类型 '{targetType}' 不接受 null");
            return null;
        }

        if (nonNullableType.IsInstanceOfType(value))
            return value;

        if (nonNullableType.IsEnum)
        {
            if (value is string stringValue)
                return Enum.Parse(nonNullableType, stringValue, true);
            var enumBaseType = Enum.GetUnderlyingType(nonNullableType);
            var enumRawValue = Convert.ChangeType(value, enumBaseType);
            return Enum.ToObject(nonNullableType, enumRawValue);
        }

        return Convert.ChangeType(value, nonNullableType);
    }

    private static Exception UnwrapException(Exception exception) =>
        exception is TargetInvocationException { InnerException: not null } tie
            ? tie.InnerException
            : exception;

    private async Task<object?> InvokeRpcMethodAsync(MethodInfo method, object?[] callParams)
    {
        object? invoked;

        try
        {
            invoked = method.Invoke(DCTravelClient, callParams);
        }
        catch (Exception ex)
        {
            throw UnwrapException(ex);
        }

        if (invoked is not Task task)
            return invoked;

        await task.ConfigureAwait(false);
        var returnType = method.ReturnType;
        if (!returnType.IsGenericType || returnType.GetGenericTypeDefinition() != typeof(Task<>))
            return null;

        var resultProperty = returnType.GetProperty("Result", BindingFlags.Public | BindingFlags.Instance)
                             ?? throw new InvalidOperationException($"无法读取方法 '{method.Name}' 的 Task 结果");
        return resultProperty.GetValue(task);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref disposeState) != 0)
            throw new ObjectDisposedException(nameof(DCTravelListener));
    }

    public sealed class RpcRequest
    {
        public string    Method { get; set; } = string.Empty;
        public object?[] Params { get; set; } = [];
    }

    public sealed class RpcResponse
    {
        public object? Result { get; set; }
        public string? Error  { get; set; }
    }

    private sealed class RpcController
    (
        DCTravelListener listener
    ) : WebApiController
    {
        [Route(HttpVerbs.Post, "/")]
        public async Task ProcessRequest()
        {
            if (!string.IsNullOrEmpty(Request.Headers["Origin"]))
            {
                Response.StatusCode = 403;
                await Response.OutputStream.WriteAsync("CORS Forbidden"u8.ToArray()).ConfigureAwait(false);
                return;
            }

            try
            {
                var rpcRequest = await ReadRequestAsync().ConfigureAwait(false);

                if (!listener.rpcMethodCache.TryGetValue(rpcRequest.Method, out var method))
                    throw new InvalidOperationException("未知或未授权的方法");

                var callParams = BindParameters(method.GetParameters(), rpcRequest.Params);
                var result     = await listener.InvokeRpcMethodAsync(method, callParams).ConfigureAwait(false);
                await WriteRpcResponseAsync(new RpcResponse { Result = result }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await WriteRpcResponseAsync(new RpcResponse { Error = UnwrapException(ex).ToString() }).ConfigureAwait(false);
            }
        }

        private async Task<RpcRequest> ReadRequestAsync()
        {
            using var reader = new StreamReader(Request.InputStream, Request.ContentEncoding ?? Encoding.UTF8);
            var       body   = await reader.ReadToEndAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
                throw new InvalidOperationException("RPC 请求体为空");

            var plainBody = listener.Decrypt(body);
            var rpcRequest = JsonSerializer.Deserialize<RpcRequest>(plainBody, SerializerOptions)
                             ?? throw new InvalidOperationException("无效的 RPC 请求负载");
            if (string.IsNullOrWhiteSpace(rpcRequest.Method))
                throw new InvalidOperationException("缺少 RPC 方法名");
            rpcRequest.Params ??= [];
            return rpcRequest;
        }

        private async Task WriteRpcResponseAsync(RpcResponse response)
        {
            var responseJson = JsonSerializer.Serialize(response, SerializerOptions);
            if (listener.useEncrypt)
                responseJson = listener.Encrypt(responseJson);

            Response.ContentType = "application/json";
            Response.StatusCode  = 200;
            await Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(responseJson)).ConfigureAwait(false);
        }
    }
}
