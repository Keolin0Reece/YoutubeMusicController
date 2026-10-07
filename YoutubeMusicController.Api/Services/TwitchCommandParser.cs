using System.Text.RegularExpressions;

namespace YoutubeMusicController.Api.Services;

internal static partial class TwitchCommandParser
{
    public static bool TryParse(
        string message,
        out ParsedTwitchCommand parsed)
    {
        var match = CommandPattern().Match(message.Trim());

        if (!match.Success)
        {
            parsed = default;
            return false;
        }

        var command = match.Groups["command"].Value.ToLowerInvariant();

        if (command is not ("song" or "nowplaying" or "np" or "queue"))
        {
            parsed = default;
            return false;
        }

        parsed = new ParsedTwitchCommand(
            command,
            match.Groups["arguments"].Value.Trim());
        return true;
    }

    [GeneratedRegex(
        @"^!(?<command>[a-z]+)(?:\s+(?<arguments>.*))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CommandPattern();
}

internal readonly record struct ParsedTwitchCommand(
    string Command,
    string Arguments);
