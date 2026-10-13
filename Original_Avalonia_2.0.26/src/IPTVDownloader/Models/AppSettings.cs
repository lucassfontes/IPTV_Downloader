using System.Text.Json;
using System.Text.Json.Serialization;

namespace IPTVDownloader.Models;

public sealed class AppSettings
{
    [JsonPropertyName("download_parent")]
    public string DownloadParent { get; set; } = "";

    [JsonPropertyName("download_root")]
    public string DownloadRootLegacy { get; set; } = "";

    [JsonPropertyName("remove_completed_from_selection")]
    public bool RemoveCompletedFromSelection { get; set; }

    [JsonPropertyName("hide_xxx_content")]
    public bool HideXxxContent { get; set; } = true;

    [JsonPropertyName("last_opened_account")]
    public string LastOpenedAccount { get; set; } = "";

    [JsonPropertyName("last_content_by_account")]
    public Dictionary<string, string> LastContentByAccount { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("last_navigation_by_account")]
    public Dictionary<string, NavigationState> LastNavigationByAccount { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Mantém chaves das versões Python que esta reescrita ainda não usa diretamente.
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class NavigationState
{
    [JsonPropertyName("view")]
    public string View { get; set; } = "catalog";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "movies";

    [JsonPropertyName("series_id")]
    public string SeriesId { get; set; } = "";

    [JsonPropertyName("series_name")]
    public string SeriesName { get; set; } = "";

    [JsonPropertyName("season")]
    public int Season { get; set; }

    [JsonPropertyName("category_id")]
    public string CategoryId { get; set; } = "";

    [JsonPropertyName("category_name")]
    public string CategoryName { get; set; } = "";

    [JsonPropertyName("query")]
    public string Query { get; set; } = "";

    [JsonPropertyName("alpha_filter")]
    public string AlphaFilter { get; set; } = "";

    [JsonPropertyName("page")]
    public int Page { get; set; } = 1;

    [JsonPropertyName("scroll_offset")]
    public double ScrollOffset { get; set; }
}
