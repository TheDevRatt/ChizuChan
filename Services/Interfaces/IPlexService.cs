using ChizuChan.DTOs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ChizuChan.Services.Interfaces
{
    public interface IPlexService
    {
        Task<StandardResponse<LookupDTO>> GetSeriesInfoAsync(string query, string apiKey, ulong userId, int page = 1, ulong messageId = 0);
        Task<StandardResponse<TvDetailsDTO>> GetTvDetailsAsync(int tvId, string apiKey, ulong userId);
        Task<StandardResponse<MovieDetailsDTO>> GetMovieDetailsAsync(int movieId, string apiKey, ulong userId);
        Task<StandardResponse<RequestMediaResultDTO>> RequestMediaAsync(RequestMediaDTO requestedMedia, string apiKey);
        string GetDownloadStatus(ResultDTO record);
    }
}
