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
        Task<List<LookupDTO>> GetSeriesInfoAsync(string query, string apiKey);
    }
}
