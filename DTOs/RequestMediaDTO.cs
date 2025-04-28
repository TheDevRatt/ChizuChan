using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace ChizuChan.DTOs
{
    public class RequestMediaDTO
    {
        [JsonPropertyName("mediaType")]
        public string MediaType { get; set; }

        [JsonPropertyName("mediaId")]
        public int MediaId { get; set; }

        [JsonPropertyName("tvdbId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? TvdbId { get; set; }

        [JsonPropertyName("seasons")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<int>? Seasons { get; set; }

        [JsonPropertyName("is4k")]
        public bool Is4K { get; set; }

        [JsonPropertyName("serverId")]
        public int ServerId { get; set; }

        [JsonPropertyName("profileId")]
        public int ProfileId { get; set; }

        [JsonPropertyName("rootFolder")]
        public string RootFolder { get; set; }

        [JsonPropertyName("languageProfileId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? LanguageProfileId { get; set; }

        [JsonPropertyName("userId")]
        public int UserId { get; set; }

        [JsonPropertyName("tags")]
        public List<int> Tags { get; set; }
    }
}
