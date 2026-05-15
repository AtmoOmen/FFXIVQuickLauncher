using System.Text.Json;
using Serilog;
using SharedMemory;

namespace XIVLauncher.ArgReader;

internal sealed class ArgReaderChannel : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new();

    private readonly RpcBuffer rpcBuffer;

    public ArgReaderChannel(string channelName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelName);
        rpcBuffer = new RpcBuffer(channelName, OnMessageReceived);
    }

    public event Action<ArgReaderMessage>? MessageReceived;

    public void Dispose() =>
        rpcBuffer.Dispose();

    public void Send(ArgReaderMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Log.Information("[ArgReaderIPC] 发送消息: {MessageType}", message.GetType().Name);
        rpcBuffer.RemoteRequest(JsonSerializer.SerializeToUtf8Bytes(message, SerializerOptions));
    }

    private void OnMessageReceived(ulong messageId, byte[] payload)
    {
        var message = JsonSerializer.Deserialize<ArgReaderMessage>(payload, SerializerOptions)
                      ?? throw new InvalidOperationException("无法反序列化 ArgReader IPC 消息");

        Log.Information("[ArgReaderIPC] 收到消息 {MessageId}: {MessageType}", messageId, message.GetType().Name);
        MessageReceived?.Invoke(message);
    }
}
