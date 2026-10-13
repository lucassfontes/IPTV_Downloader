using System.Text.Json.Serialization;

namespace IPTVDownloader.Models;

public sealed class XtreamAccount
{
    [JsonPropertyName("server")]
    public string Server { get; set; } = "";

    [JsonPropertyName("username")]
    public string Username { get; set; } = "";

    [JsonPropertyName("password")]
    public string Password { get; set; } = "";

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Username) ? "Lista" : Username;
}
