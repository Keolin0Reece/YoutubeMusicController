using YoutubeMusicController.Api.Services;

namespace YoutubeMusicController.Tests;

public sealed class TwitchCommandParserTests
{
    [Theory]
    [InlineData("hello chat")]
    [InlineData("bad romance")]
    [InlineData("!sr bad romance")]
    [InlineData("!songs bad romance")]
    [InlineData("!unknown")]
    public void NonCommandsDoNotTriggerSongSearch(string message)
    {
        Assert.False(TwitchCommandParser.TryParse(message, out _));
    }

    [Theory]
    [InlineData("!song bad romance", "bad romance")]
    [InlineData("  !SONG   title and artist  ", "title and artist")]
    public void SongCommandIsParsed(string message, string expectedQuery)
    {
        Assert.True(TwitchCommandParser.TryParse(message, out var parsed));
        Assert.Equal("song", parsed.Command);
        Assert.Equal(expectedQuery, parsed.Arguments);
    }

    [Theory]
    [InlineData("!nowplaying", "nowplaying")]
    [InlineData("!np", "np")]
    [InlineData("!queue", "queue")]
    public void ReadOnlyCommandsAreParsed(string message, string command)
    {
        Assert.True(TwitchCommandParser.TryParse(message, out var parsed));
        Assert.Equal(command, parsed.Command);
    }
}
