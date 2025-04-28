using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace ChizuChan.DTOs
{
    public class RequestMediaResultDTO
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("status")]
        public int Status { get; set; }

        [JsonPropertyName("media")]
        public MediaDTO Media { get; set; }

        [JsonPropertyName("createdAt")]
        public DateTime CreatedAt { get; set; }

        [JsonPropertyName("updatedAt")]
        public DateTime UpdatedAt { get; set; }

        [JsonPropertyName("requestedBy")]
        public UserDTO RequestedBy { get; set; }

        [JsonPropertyName("modifiedBy")]
        public UserDTO ModifiedBy { get; set; }

        [JsonPropertyName("is4k")]
        public bool Is4K { get; set; }

        [JsonPropertyName("serverId")]
        public int ServerId { get; set; }

        [JsonPropertyName("profileId")]
        public int ProfileId { get; set; }

        [JsonPropertyName("rootFolder")]
        public string RootFolder { get; set; }
    }

    public class MediaDTO
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("tmdbId")]
        public int TmdbId { get; set; }

        [JsonPropertyName("tvdbId")]
        public int? TvdbId { get; set; }

        [JsonPropertyName("status")]
        public int Status { get; set; }

        [JsonPropertyName("requests")]
        public List<string> Requests { get; set; }

        [JsonPropertyName("createdAt")]
        public DateTime CreatedAt { get; set; }

        [JsonPropertyName("updatedAt")]
        public DateTime UpdatedAt { get; set; }
    }
}
