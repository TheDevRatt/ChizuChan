using ChizuChan.Services.Interfaces;
using Microsoft.Extensions.Logging;
using System.Text;
using Whisper.net;
using Whisper.net.Ggml;

namespace ChizuChan.Services
{
    /// <summary>
    /// Transcribes WAV audio using a local Whisper ggml model (tiny.en, ~77 MB).
    /// The model is downloaded automatically on first use.
    /// </summary>
    public class WhisperSttService : IWhisperSttService, IAsyncDisposable
    {
        private static readonly string ModelPath =
            Path.Combine(AppContext.BaseDirectory, "ggml-tiny.en.bin");

        private readonly ILogger<WhisperSttService> _logger;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private WhisperFactory? _factory;

        public WhisperSttService(ILogger<WhisperSttService> logger)
        {
            _logger = logger;
        }

        public async Task<string> TranscribeAsync(byte[] wavBytes, CancellationToken ct = default)
        {
            await EnsureInitializedAsync(ct);

            await _lock.WaitAsync(ct);
            try
            {
                using var processor = _factory!
                    .CreateBuilder()
                    .WithLanguage("en")
                    .Build();

                using var ms = new MemoryStream(wavBytes);
                var sb = new StringBuilder();

                await foreach (var segment in processor.ProcessAsync(ms, ct))
                {
                    var text = segment.Text?.Trim();
                    if (!string.IsNullOrEmpty(text) && text != "[BLANK_AUDIO]")
                        sb.Append(text).Append(' ');
                }

                return sb.ToString().Trim();
            }
            finally
            {
                _lock.Release();
            }
        }

        private async Task EnsureInitializedAsync(CancellationToken ct)
        {
            if (_factory is not null) return;

            await _lock.WaitAsync(ct);
            try
            {
                if (_factory is not null) return;

                if (!File.Exists(ModelPath))
                {
                    _logger.LogInformation("[Whisper] Downloading tiny.en model (~77 MB) to {Path}...", ModelPath);
                    using var modelStream = await WhisperGgmlDownloader.Default
                        .GetGgmlModelAsync(GgmlType.TinyEn, QuantizationType.NoQuantization, ct);
                    await using var fileStream = File.Create(ModelPath);
                    await modelStream.CopyToAsync(fileStream, ct);
                    _logger.LogInformation("[Whisper] Model downloaded.");
                }

                try
                {
                    _factory = WhisperFactory.FromPath(ModelPath, new WhisperFactoryOptions { GpuDevice = 0 });
                    _logger.LogInformation("[Whisper] Initialized with CUDA GPU (device 0) from {Path}", ModelPath);
                }
                catch (Exception gpuEx)
                {
                    _logger.LogWarning("[Whisper] CUDA unavailable ({Msg}), falling back to CPU", gpuEx.Message);
                    _factory = WhisperFactory.FromPath(ModelPath);
                    _logger.LogInformation("[Whisper] Initialized with CPU runtime from {Path}", ModelPath);
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            _factory?.Dispose();
            _lock.Dispose();
            await ValueTask.CompletedTask;
        }
    }
}
