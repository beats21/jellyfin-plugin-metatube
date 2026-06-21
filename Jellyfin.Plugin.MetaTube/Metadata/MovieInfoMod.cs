using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.MetaTube.Metadata;

// MovieInfoMod is the enriched payload returned by the local companion service.
// It is a MovieInfo plus an actor name dictionary (JP key -> EN value) and a rating,
// so it can flow through the same translation pipeline as MovieInfo.
public class MovieInfoMod : MovieInfo
{
    [JsonPropertyName("actors_dict")]
    public Dictionary<string, string> ActorsDict { get; set; }

    [JsonPropertyName("rating")]
    public string Rating { get; set; }

    // The companion identifies movies by `code` + `source` rather than the
    // upstream `id`/`number`/`provider` fields. Map them onto the base
    // properties the provider pipeline (SetPid, templates, image URLs) expects.
    [JsonPropertyName("code")]
    public string Code
    {
        get => Number;
        set
        {
            Id = value;
            Number = value;
        }
    }

    [JsonPropertyName("source")]
    public string Source
    {
        get => Provider;
        set => Provider = value;
    }
}
