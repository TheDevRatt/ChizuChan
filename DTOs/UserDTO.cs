using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace ChizuChan.DTOs
{
    public class UserDTO
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("email")]
        public string Email { get; set; }

        [JsonPropertyName("username")]
        public string Username { get; set; }

        [JsonPropertyName("plexToken")]
        public string PlexToken { get; set; }

        [JsonPropertyName("plexUsername")]
        public string PlexUsername { get; set; }

        [JsonPropertyName("userType")]
        public int UserType { get; set; }

        [JsonPropertyName("permissions")]
        public int Permissions { get; set; }

        [JsonPropertyName("avatar")]
        public string Avatar { get; set; }

        [JsonPropertyName("createdAt")]
        public DateTime CreatedAt { get; set; }

        [JsonPropertyName("updatedAt")]
        public DateTime UpdatedAt { get; set; }

        [JsonPropertyName("requestCount")]
        public int RequestCount { get; set; }
    }
}
