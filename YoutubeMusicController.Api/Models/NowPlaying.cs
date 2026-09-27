namespace YoutubeMusicController.Api.Models;

public sealed class NowPlaying
{
    public string? Title { get; set; }
    public string? AlternativeTitle { get; set; }
    public string? Artist { get; set; }
    public string? ArtistUrl { get; set; }
    public long Views { get; set; }
    public DateTimeOffset? UploadDate { get; set; }
    public string? ImageSrc { get; set; }
    public bool IsPaused { get; set; }
    public double SongDuration { get; set; }
    public double ElapsedSeconds { get; set; }
    public string? Url { get; set; }
    public string? Album { get; set; }
    public string? VideoId { get; set; }
    public string? PlaylistId { get; set; }
    public string? MediaType { get; set; }
    public List<string> Tags { get; set; } = [];
}