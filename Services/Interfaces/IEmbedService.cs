using ChizuChan.DTOs;
using NetCord.Rest;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ChizuChan.Services.Interfaces
{
    public interface IEmbedService
    {
        (EmbedProperties Embed, IComponentProperties[] Components) BuildSearchEmbed(ResultDTO record, int index, int total, int page, int totalPages);
        ModalProperties BuildSearchModal(ResultDTO record, int index, int page);
    }
}
