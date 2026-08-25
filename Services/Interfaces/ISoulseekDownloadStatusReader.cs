using ChizuChan.DTOs;

namespace ChizuChan.Services.Interfaces;

public interface ISoulseekDownloadStatusReader
{
    Task<StandardResponse<SoulseekDownloadBatchStatusDTO>> GetBatchStatusAsync(
        Guid batchId,
        CancellationToken cancellationToken = default);
}
