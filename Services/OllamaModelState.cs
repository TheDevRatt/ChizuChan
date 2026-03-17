using ChizuChan.Options;
using ChizuChan.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace ChizuChan.Services
{
    public class OllamaModelState : IOllamaModelState
    {
        // Models that support image input
        private static readonly HashSet<string> VisionModels = new(StringComparer.OrdinalIgnoreCase)
        {
            "gemma3:4b",
            "gemma3:12b"
        };

        private volatile string _currentModel;

        public OllamaModelState(IOptions<OllamaOptions> options)
        {
            _currentModel = options.Value.Model;
        }

        public string CurrentModel => _currentModel;
        public bool IsVisionModel => VisionModels.Contains(_currentModel);
        public void SetModel(string model) => _currentModel = model;
    }
}
