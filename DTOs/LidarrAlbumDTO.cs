using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChizuChan.DTOs;

public class LidarrAlbumDTO
{
    public int? Id { get; set; }
    public string? ForeignAlbumId { get; set; }
    public string? Title { get; set; }
    public DateTimeOffset? ReleaseDate { get; set; }
    public string? Overview { get; set; }
    public string? AlbumType { get; set; }
    public LidarrArtistDTO? Artist { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}

public class LidarrArtistDTO
{
    public int? Id { get; set; }
    public string? ForeignArtistId { get; set; }
    public string? ArtistName { get; set; }
    public string? Overview { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}

public class LidarrAlbumRequestResultDTO
{
    public int AlbumId { get; set; }
    public string ForeignAlbumId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string ArtistName { get; set; } = string.Empty;
    public bool AlreadyExists { get; set; }
}
