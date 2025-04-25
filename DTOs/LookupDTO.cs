// Auto-generated DTO based on full API shape with detailed episode tracking
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ChizuChan.DTOs
{
    public class LookupDTO
    {
        [JsonPropertyName("page")]
        public int Page { get; set; }

        [JsonPropertyName("totalPages")]
        public int TotalPages { get; set; }

        [JsonPropertyName("totalResults")]
        public int TotalResults { get; set; }

        [JsonPropertyName("results")]
        public List<ResultDTO> Results { get; set; }
    }

    public class ResultDTO
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("mediaType")]
        public string MediaType { get; set; }

        [JsonPropertyName("popularity")]
        public double? Popularity { get; set; }

        [JsonPropertyName("posterPath")]
        public string PosterPath { get; set; }

        [JsonPropertyName("backdropPath")]
        public string BackdropPath { get; set; }

        [JsonPropertyName("voteCount")]
        public int? VoteCount { get; set; }

        [JsonPropertyName("voteAverage")]
        public double? VoteAverage { get; set; }

        [JsonPropertyName("genreIds")]
        public List<int> GenreIds { get; set; }

        [JsonPropertyName("overview")]
        public string Overview { get; set; }

        [JsonPropertyName("originalLanguage")]
        public string OriginalLanguage { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; }

        [JsonPropertyName("originalTitle")]
        public string OriginalTitle { get; set; }

        [JsonPropertyName("releaseDate")]
        public string ReleaseDate { get; set; }

        [JsonPropertyName("adult")]
        public bool? Adult { get; set; }

        [JsonPropertyName("video")]
        public bool? Video { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("originalName")]
        public string OriginalName { get; set; }

        [JsonPropertyName("originCountry")]
        public List<string> OriginCountry { get; set; }

        [JsonPropertyName("firstAirDate")]
        public string FirstAirDate { get; set; }

        [JsonPropertyName("profilePath")]
        public string ProfilePath { get; set; }

        [JsonPropertyName("knownFor")]
        public List<ResultDTO> KnownFor { get; set; }

        [JsonPropertyName("mediaInfo")]
        public MediaInfoDTO MediaInfo { get; set; }
    }

    public class MediaInfoDTO
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("tmdbId")]
        public int TmdbId { get; set; }

        [JsonPropertyName("tvdbId")]
        public int TvdbId { get; set; }

        [JsonPropertyName("status")]
        public int Status { get; set; }

        [JsonPropertyName("requests")]
        public List<RequestDTO> Requests { get; set; }

        [JsonPropertyName("seasons")]
        public List<SeasonDTO> Seasons { get; set; }

        [JsonPropertyName("createdAt")]
        public DateTime CreatedAt { get; set; }

        [JsonPropertyName("updatedAt")]
        public DateTime UpdatedAt { get; set; }

        [JsonPropertyName("plexUrl")]
        public string PlexUrl { get; set; }
    }

    public class SeasonDTO
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("seasonNumber")]
        public int SeasonNumber { get; set; }

        [JsonPropertyName("status")]
        public int Status { get; set; }

        [JsonPropertyName("status4k")]
        public int Status4k { get; set; }

        [JsonPropertyName("episodeCount")]
        public int EpisodeCount { get; set; }

        [JsonPropertyName("episodeFileCount")]
        public int EpisodeFileCount { get; set; }

        [JsonPropertyName("createdAt")]
        public DateTime CreatedAt { get; set; }

        [JsonPropertyName("updatedAt")]
        public DateTime UpdatedAt { get; set; }
    }

    public class RequestDTO
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("status")]
        public int Status { get; set; }

        [JsonPropertyName("media")]
        public string Media { get; set; }

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