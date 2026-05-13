namespace XIVLauncher.Common.Game.Exceptions;

public class InvalidResponseException
(
    string message,
    string document
) : Exception(message)
{
    public string Document { get; set; } = document;
}
