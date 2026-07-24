using ChizuChan.DTOs;
using NetCord.Rest;

namespace ChizuChan.Services.Interfaces;

public interface IMusicSearchEmbedBuilder
{
    (EmbedProperties Embed, IMessageComponentProperties[] Components) Build(MusicSearchSessionSnapshot session);

    // Compatibility for the existing slash-command response until it binds a paginated session message.
    EmbedProperties Build(string query, MusicSearchResultsDTO results);
}
