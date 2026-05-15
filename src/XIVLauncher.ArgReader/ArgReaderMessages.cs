using System.Text.Json.Serialization;
using XIVLauncher.Common.Game;

namespace XIVLauncher.ArgReader;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(HelloMessage),           "hello")]
[JsonDerivedType(typeof(ReadLoginDataRequest),   "read_login_data_request")]
[JsonDerivedType(typeof(ReadLoginDataSucceeded), "read_login_data_succeeded")]
[JsonDerivedType(typeof(CommandFailed),          "command_failed")]
[JsonDerivedType(typeof(ShutdownRequest),        "shutdown_request")]
[JsonDerivedType(typeof(GoodbyeMessage),         "goodbye")]
internal abstract record ArgReaderMessage;

internal sealed record HelloMessage : ArgReaderMessage;

internal sealed record ReadLoginDataRequest(int ProcessId) : ArgReaderMessage;

internal sealed record ReadLoginDataSucceeded(GameArgumentInterop.LoginData Data) : ArgReaderMessage;

internal sealed record CommandFailed(string ErrorMessage, string Details) : ArgReaderMessage;

internal sealed record ShutdownRequest(bool KillTargetProcess, int? TargetProcessId) : ArgReaderMessage;

internal sealed record GoodbyeMessage : ArgReaderMessage;
