using ChizuChan.DTOs;
using NetCord.Rest;

namespace ChizuChan.Services.Interfaces;

public interface IMusicSearchEmbedBuilder
{
    EmbedProperties Build(string query, MusicSearchResultsDTO results);
}
