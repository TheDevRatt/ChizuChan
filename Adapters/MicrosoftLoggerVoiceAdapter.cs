using Microsoft.Extensions.Logging;
using NetCord.Logging;
using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;
using NcLogLevel = NetCord.Logging.LogLevel;

namespace ChizuChan.Adapters
{
    /// <summary>
    /// Bridges NetCord's IVoiceLogger to Microsoft.Extensions.Logging.ILogger
    /// so internal voice WebSocket events (including disconnect close codes) appear in application logs.
    /// </summary>
    public sealed class MicrosoftLoggerVoiceAdapter : IVoiceLogger
    {
        private readonly ILogger _logger;

        public MicrosoftLoggerVoiceAdapter(ILogger logger)
        {
            _logger = logger;
        }

        public bool IsEnabled(NcLogLevel logLevel) =>
            _logger.IsEnabled(Map(logLevel));

        public void Log<TState>(NcLogLevel logLevel, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _logger.Log(Map(logLevel), 0, state, exception, formatter);

        private static MsLogLevel Map(NcLogLevel level) => level switch
        {
            NcLogLevel.Trace       => MsLogLevel.Trace,
            NcLogLevel.Debug       => MsLogLevel.Debug,
            NcLogLevel.Information => MsLogLevel.Information,
            NcLogLevel.Warning     => MsLogLevel.Warning,
            NcLogLevel.Error       => MsLogLevel.Error,
            NcLogLevel.Critical    => MsLogLevel.Critical,
            _                      => MsLogLevel.None,
        };
    }
}
