// O modelo web mantém os campos de dados do MediaItem original.
// Apenas comandos, bitmaps e geometrias Avalonia foram retirados,
// pois a interface é renderizada pelo navegador.
using System.Text.Json.Serialization;
namespace IPTVDownloader.Models;
public sealed class MediaItem
{
    [JsonPropertyName("type")] public string Type { get; set; } = "movie";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("iptv_name")] public string IptvName { get; set; } = "";
    [JsonPropertyName("group")] public string Group { get; set; } = "";
    [JsonPropertyName("category_id")] public string CategoryId { get; set; } = "";
    [JsonPropertyName("logo")] public string Logo { get; set; } = "";
    [JsonPropertyName("stream_id")] public string StreamId { get; set; } = "";
    [JsonPropertyName("series_id")] public string SeriesId { get; set; } = "";
    [JsonPropertyName("episode_id")] public string EpisodeId { get; set; } = "";
    [JsonPropertyName("series_name")] public string SeriesName { get; set; } = "";
    [JsonPropertyName("season")] public int Season { get; set; }
    [JsonPropertyName("episode_num")] public int EpisodeNumber { get; set; }
    [JsonPropertyName("container_extension")] public string ContainerExtension { get; set; } = "mp4";
    [JsonPropertyName("direct_source")] public string DirectSource { get; set; } = "";
    [JsonPropertyName("year")] public string Year { get; set; } = "";
    [JsonPropertyName("genre")] public string Genre { get; set; } = "";
    [JsonPropertyName("plot")] public string Plot { get; set; } = "";
    [JsonPropertyName("rating")] public string Rating { get; set; } = "";
    [JsonPropertyName("cast")] public string Cast { get; set; } = "";
    [JsonPropertyName("director")] public string Director { get; set; } = "";
    [JsonPropertyName("duration")] public string Duration { get; set; } = "";
    [JsonPropertyName("duration_secs")] public long DurationSeconds { get; set; }
    [JsonPropertyName("episode_run_time")] public string EpisodeRunTime { get; set; } = "";
    [JsonPropertyName("release_date")] public string ReleaseDate { get; set; } = "";
    [JsonPropertyName("season_count")] public int? SeasonCount { get; set; }
    [JsonPropertyName("episodes")] public List<MediaItem> Episodes { get; set; } = [];
    [JsonIgnore] public string Status { get; set; } = "";
    [JsonIgnore] public string MetaText { get; set; } = "";
    [JsonIgnore] public string PlotText { get; set; } = "";
    [JsonIgnore] public bool IsMovie => Type == "movie";
    [JsonIgnore] public bool IsEpisode => Type == "episode";
    [JsonIgnore] public bool IsSeries => Type == "series";
    [JsonIgnore] public bool IsSeason => Type == "season";
    [JsonIgnore] public string Identity => Type switch
    {
        "movie" => $"movie:{StreamId}",
        "series" => $"series:{SeriesId}",
        "season" => $"season:{SeriesId}:{Season}",
        "episode" => $"episode:{EpisodeId}",
        _ => $"{Type}:{Name}"
    };
}
