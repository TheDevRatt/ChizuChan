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
        Task<LookupDTO?> GetSeriesInfoAsync(string query, string apiKey, ulong userId, int page = 1, ulong messageId = 0);
        string GetDownloadStatus(ResultDTO record);
    }
}
