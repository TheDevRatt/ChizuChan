using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace ChizuChan.DTOs
{
    public class MovieDetailsDTO
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("imdbId")]
        public string ImdbId { get; set; }

        [JsonPropertyName("adult")]
        public bool Adult { get; set; }

        [JsonPropertyName("backdropPath")]
        public string BackdropPath { get; set; }

        [JsonPropertyName("posterPath")]
        public string PosterPath { get; set; }

        [JsonPropertyName("budget")]
        public int Budget { get; set; }

        [JsonPropertyName("genres")]
        public List<GenreDTO> Genres { get; set; }

        [JsonPropertyName("homepage")]
        public string Homepage { get; set; }

        [JsonPropertyName("relatedVideos")]
        public List<RelatedVideoDTO> RelatedVideos { get; set; }

        [JsonPropertyName("originalLanguage")]
        public string OriginalLanguage { get; set; }

        [JsonPropertyName("originalTitle")]
        public string OriginalTitle { get; set; }

        [JsonPropertyName("overview")]
        public string Overview { get; set; }

        [JsonPropertyName("popularity")]
        public double Popularity { get; set; }

        [JsonPropertyName("productionCompanies")]
        public List<ProductionCompanyDTO> ProductionCompanies { get; set; }

        [JsonPropertyName("productionCountries")]
        public List<ProductionCountryDTO> ProductionCountries { get; set; }

        [JsonPropertyName("releaseDate")]
        public string ReleaseDate { get; set; }

        [JsonPropertyName("releases")]
        public ReleasesDTO Releases { get; set; }

        [JsonPropertyName("revenue")]
        public int Revenue { get; set; }

        [JsonPropertyName("runtime")]
        public int Runtime { get; set; }

        [JsonPropertyName("spokenLanguages")]
        public List<SpokenLanguageDTO> SpokenLanguages { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; }

        [JsonPropertyName("tagline")]
        public string Tagline { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; }

        [JsonPropertyName("video")]
        public bool Video { get; set; }

        [JsonPropertyName("voteAverage")]
        public double VoteAverage { get; set; }

        [JsonPropertyName("voteCount")]
        public int VoteCount { get; set; }

        [JsonPropertyName("credits")]
        public CreditsDTO Credits { get; set; }

        [JsonPropertyName("collection")]
        public CollectionDTO Collection { get; set; }

        [JsonPropertyName("externalIds")]
        public ExternalIdsDTO ExternalIds { get; set; }

        [JsonPropertyName("mediaInfo")]
        public MediaInfoDTO MediaInfo { get; set; }

        [JsonPropertyName("watchProviders")]
        public List<WatchProviderDTO> WatchProviders { get; set; }

        [JsonPropertyName("keywords")]
        public List<KeywordDTO> Keywords { get; set; }
    }

    public class GenreDTO
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }
    }

    public class RelatedVideoDTO
    {
        [JsonPropertyName("url")]
        public string Url { get; set; }

        [JsonPropertyName("key")]
        public string Key { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("size")]
        public int Size { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; }

        [JsonPropertyName("site")]
        public string Site { get; set; }
    }

    public class ProductionCompanyDTO
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

    public class ProductionCountryDTO
    {
        [JsonPropertyName("iso_3166_1")]
        public string Iso3166_1 { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }
    }

    public class ReleasesDTO
    {
        [JsonPropertyName("results")]
        public List<ReleaseResultDTO> Results { get; set; }
    }

    public class ReleaseResultDTO
    {
        [JsonPropertyName("iso_3166_1")]
        public string Iso3166_1 { get; set; }

        [JsonPropertyName("rating")]
        public string Rating { get; set; }

        [JsonPropertyName("release_dates")]
        public List<ReleaseDateDTO> ReleaseDates { get; set; }
    }

    public class ReleaseDateDTO
    {
        [JsonPropertyName("certification")]
        public string Certification { get; set; }

        [JsonPropertyName("iso_639_1")]
        public string Iso639_1 { get; set; }

        [JsonPropertyName("note")]
        public string Note { get; set; }

        [JsonPropertyName("release_date")]
        public DateTime ReleaseDate { get; set; }

        [JsonPropertyName("type")]
        public int Type { get; set; }
    }

    public class SpokenLanguageDTO
    {
        [JsonPropertyName("englishName")]
        public string EnglishName { get; set; }

        [JsonPropertyName("iso_639_1")]
        public string Iso639_1 { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }
    }

    public class CreditsDTO
    {
        [JsonPropertyName("cast")]
        public List<CastDTO> Cast { get; set; }

        [JsonPropertyName("crew")]
        public List<CrewDTO> Crew { get; set; }
    }

    public class CastDTO
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("castId")]
        public int CastId { get; set; }

        [JsonPropertyName("character")]
        public string Character { get; set; }

        [JsonPropertyName("creditId")]
        public string CreditId { get; set; }

        [JsonPropertyName("gender")]
        public int Gender { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("order")]
        public int Order { get; set; }

        [JsonPropertyName("profilePath")]
        public string ProfilePath { get; set; }
    }

    public class CrewDTO
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("creditId")]
        public string CreditId { get; set; }

        [JsonPropertyName("gender")]
        public int Gender { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("job")]
        public string Job { get; set; }

        [JsonPropertyName("department")]
        public string Department { get; set; }

        [JsonPropertyName("profilePath")]
        public string ProfilePath { get; set; }
    }

    public class CollectionDTO
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("posterPath")]
        public string PosterPath { get; set; }

        [JsonPropertyName("backdropPath")]
        public string BackdropPath { get; set; }
    }

    public class ExternalIdsDTO
    {
        [JsonPropertyName("facebookId")]
        public string FacebookId { get; set; }

        [JsonPropertyName("freebaseId")]
        public string FreebaseId { get; set; }

        [JsonPropertyName("freebaseMid")]
        public string FreebaseMid { get; set; }

        [JsonPropertyName("imdbId")]
        public string ImdbId { get; set; }

        [JsonPropertyName("instagramId")]
        public string InstagramId { get; set; }

        [JsonPropertyName("tvdbId")]
        public int? TvdbId { get; set; }

        [JsonPropertyName("tvrageId")]
        public int? TvrageId { get; set; }

        [JsonPropertyName("twitterId")]
        public string TwitterId { get; set; }
    }

    public class WatchProviderDTO
    {
        [JsonPropertyName("iso_3166_1")]
        public string Iso3166_1 { get; set; }

        [JsonPropertyName("link")]
        public string Link { get; set; }

        [JsonPropertyName("buy")]
        public List<ProviderDetailsDTO> Buy { get; set; }

        [JsonPropertyName("flatrate")]
        public List<ProviderDetailsDTO> Flatrate { get; set; }
    }

    public class ProviderDetailsDTO
    {
        [JsonPropertyName("displayPriority")]
        public int DisplayPriority { get; set; }

        [JsonPropertyName("logoPath")]
        public string LogoPath { get; set; }

        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }
    }
}
