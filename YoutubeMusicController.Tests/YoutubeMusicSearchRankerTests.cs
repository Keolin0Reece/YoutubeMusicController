using YoutubeMusicController.Api.Models;
using YoutubeMusicController.Api.Services;

namespace YoutubeMusicController.Tests;

public sealed class YoutubeMusicSearchRankerTests
{
    [Fact]
    public void CloseMatchAcceptsTitleAndArtist()
    {
        var result = new SearchResult(
            "Bad Romance",
            ["Lady Gaga"],
            null,
            null,
            null,
            "video-id",
            "Video");

        Assert.True(YoutubeMusicSearchRanker.IsCloseMatch(
            "Bad Romance by Lady Gaga",
            result));
    }

    [Fact]
    public void CloseMatchRejectsDifferentTitle()
    {
        var result = new SearchResult(
            "Poker Face",
            ["Lady Gaga"],
            null,
            null,
            null,
            "video-id",
            "Video");

        Assert.False(YoutubeMusicSearchRanker.IsCloseMatch(
            "Bad Romance by Lady Gaga",
            result));
    }
}
