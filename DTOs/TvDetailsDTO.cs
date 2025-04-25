using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace ChizuChan.DTOs
{
    public class TvDetailsDTO
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("backdropPath")]
        public string BackdropPath { get; set; }

        [JsonPropertyName("posterPath")]
        public string PosterPath { get; set; }

        [JsonPropertyName("contentRatings")]
        public ContentRatingsDTO ContentRatings { get; set; }

        [JsonPropertyName("createdBy")]
        public List<CreatorDTO> CreatedBy { get; set; }

        [JsonPropertyName("episodeRunTime")]
        public List<int> EpisodeRunTime { get; set; }

        [JsonPropertyName("firstAirDate")]
        public string FirstAirDate { get; set; }

        [JsonPropertyName("genres")]
        public List<GenreDTO> Genres { get; set; }

        [JsonPropertyName("homepage")]
        public string Homepage { get; set; }

        [JsonPropertyName("inProduction")]
        public bool InProduction { get; set; }

        [JsonPropertyName("languages")]
        public List<string> Languages { get; set; }

        [JsonPropertyName("lastAirDate")]
        public string LastAirDate { get; set; }

        [JsonPropertyName("lastEpisodeToAir")]
        public EpisodeDTO LastEpisodeToAir { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("nextEpisodeToAir")]
        public EpisodeDTO NextEpisodeToAir { get; set; }

        [JsonPropertyName("networks")]
        public List<NetworkDTO> Networks { get; set; }

        [JsonPropertyName("numberOfEpisodes")]
        public int NumberOfEpisodes { get; set; }

        [JsonPropertyName("numberOfSeason")]
        public int NumberOfSeason { get; set; }

        [JsonPropertyName("originCountry")]
        public List<string> OriginCountry { get; set; }

        [JsonPropertyName("originalLanguage")]
        public string OriginalLanguage { get; set; }

        [JsonPropertyName("originalName")]
        public string OriginalName { get; set; }

        [JsonPropertyName("overview")]
        public string Overview { get; set; }

        [JsonPropertyName("popularity")]
        public double Popularity { get; set; }

        [JsonPropertyName("productionCompanies")]
        public List<ProductionCompanyDTO> ProductionCompanies { get; set; }

        [JsonPropertyName("productionCountries")]
        public List<ProductionCountryDTO> ProductionCountries { get; set; }

        [JsonPropertyName("spokenLanguages")]
        public List<SpokenLanguageDTO> SpokenLanguages { get; set; }

        [JsonPropertyName("seasons")]
        public List<SeasonDetailsDTO> Seasons { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; }

        [JsonPropertyName("tagline")]
        public string Tagline { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; }

        [JsonPropertyName("voteAverage")]
        public double VoteAverage { get; set; }

        [JsonPropertyName("voteCount")]
        public int VoteCount { get; set; }

        [JsonPropertyName("credits")]
        public CreditsDTO Credits { get; set; }

        [JsonPropertyName("externalIds")]
        public ExternalIdsDTO ExternalIds { get; set; }

        [JsonPropertyName("keywords")]
        public List<KeywordDTO> Keywords { get; set; }

        [JsonPropertyName("mediaInfo")]
        public MediaInfoDTO MediaInfo { get; set; }
    }

    public class ContentRatingsDTO
    {
        [JsonPropertyName("results")]
        public List<ContentRatingResultDTO> Results { get; set; }
    }

    public class ContentRatingResultDTO
    {
        [JsonPropertyName("iso_3166_1")]
        public string Iso3166_1 { get; set; }

        [JsonPropertyName("rating")]
        public string Rating { get; set; }
    }

    public class CreatorDTO
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("gender")]
        public int Gender { get; set; }

        [JsonPropertyName("profilePath")]
        public string ProfilePath { get; set; }
    }

    public class EpisodeDTO
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("airDate")]
        public string AirDate { get; set; }

        [JsonPropertyName("episodeNumber")]
        public int EpisodeNumber { get; set; }

        [JsonPropertyName("overview")]
        public string Overview { get; set; }

        [JsonPropertyName("productionCode")]
        public string ProductionCode { get; set; }

        [JsonPropertyName("seasonNumber")]
        public int SeasonNumber { get; set; }

        [JsonPropertyName("showId")]
        public int ShowId { get; set; }

        [JsonPropertyName("stillPath")]
        public string StillPath { get; set; }

        [JsonPropertyName("voteAverage")]
        public double VoteAverage { get; set; }

        [JsonPropertyName("voteCount")]
        public int VoteCount { get; set; }
    }

    public class NetworkDTO
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("logoPath")]
        public string LogoPath { get; set; }

        [JsonPropertyName("originCountry")]
        public string OriginCountry { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }
    }

    public class SeasonDetailsDTO
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("airDate")]
        public string AirDate { get; set; }

        [JsonPropertyName("episodeCount")]
        public int EpisodeCount { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("overview")]
        public string Overview { get; set; }

        [JsonPropertyName("posterPath")]
        public string PosterPath { get; set; }

        [JsonPropertyName("seasonNumber")]
        public int SeasonNumber { get; set; }

        [JsonPropertyName("episodes")]
        public List<EpisodeDTO> Episodes { get; set; }
    }

    public class KeywordDTO
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }
    }
}
