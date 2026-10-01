using System;

namespace Joycon2PC.Core.Logging
{
    /// <summary>
    /// 日志级别
    /// </summary>
    public enum LogLevel
    {
        Debug,
        Info,
        Warning,
        Error
    }

    /// <summary>
    /// 简单的日志接口
    /// </summary>
    public interface ILogger
    {
        void Log(LogLevel level, string message);
        void Log(LogLevel level, string message, Exception exception);
        
        void Debug(string message);
        void Info(string message);
        void Warning(string message);
        void Error(string message);
        void Error(string message, Exception exception);
    }

    /// <summary>
    /// 控制台日志实现
    /// </summary>
    public class ConsoleLogger : ILogger
    {
        private readonly string _category;

        public ConsoleLogger(string category = "")
        {
            _category = category;
        }

        public void Log(LogLevel level, string message)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            var categoryStr = string.IsNullOrEmpty(_category) ? "" : $" [{_category}]";
            var levelStr = level switch
            {
                LogLevel.Debug => "DBG",
                LogLevel.Info => "INF",
                LogLevel.Warning => "WRN",
                LogLevel.Error => "ERR",
                _ => "???"
            };
            
            Console.WriteLine($"[{timestamp}] [{levelStr}]{categoryStr} {message}");
        }

        public void Log(LogLevel level, string message, Exception exception)
        {
            Log(level, $"{message}: {exception.Message}");
        }

        public void Debug(string message) => Log(LogLevel.Debug, message);
        public void Info(string message) => Log(LogLevel.Info, message);
        public void Warning(string message) => Log(LogLevel.Warning, message);
        public void Error(string message) => Log(LogLevel.Error, message);
        public void Error(string message, Exception exception) => Log(LogLevel.Error, message, exception);
    }

    /// <summary>
    /// 日志工厂
    /// </summary>
    public static class LoggerFactory
    {
        private static ILogger _defaultLogger = new ConsoleLogger();

        public static ILogger CreateLogger(string category)
        {
            return new ConsoleLogger(category);
        }

        public static void SetDefaultLogger(ILogger logger)
        {
            _defaultLogger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public static ILogger Default => _defaultLogger;
    }
}
