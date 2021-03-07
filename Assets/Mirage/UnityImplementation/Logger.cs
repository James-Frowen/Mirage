
using System;

namespace UnityEngine
{
    public class LoggerHandler : ILogHandler
    {
        public static ILogHandler Default => new LoggerHandler();

        public void LogException(Exception exception)
        {
            LogFormat(LogType.Exception, null, "Exception: {0} message: {1}", exception.GetType().Name, exception.Message);
        }

        public void LogFormat(LogType logType, string format, params object[] args)
        {
            string msg = string.Format(format, args);
            Console.WriteLine($"{logType}: {msg}");
        }
    }

    public class Logger : ILogger
    {
        public static ILogger Default => new Logger();

        public LogType filterLogType { get; set; }
        public ILogHandler logHandler { get; set; } = LoggerHandler.Default;
        public bool logEnabled => throw new NotImplementedException();

        public bool IsLogTypeAllowed(LogType logType)
        {
            if (logType == LogType.Exception) { return true; }
            else
            {
                return (int)logType <= (int)filterLogType;
            }
        }

        public void Log(LogType logType, object message)
        {
            logHandler.LogFormat(logType, (string)message);
        }

        public void LogException(Exception exception)
        {
            logHandler.LogException(exception);
        }

        public void LogFormat(LogType logType, string format, params object[] args)
        {
            logHandler.LogFormat(logType, format, args);
        }
    }
    public enum LogType
    {
        Error = 0,
        Assert = 1,
        Warning = 2,
        Log = 3,
        Exception = 4
    }
    public interface ILogHandler
    {
        void LogException(Exception exception);
        void LogFormat(LogType logType, string format, params object[] args);
    }
    public interface ILogger : ILogHandler
    {
        LogType filterLogType { get; set; }
        ILogHandler logHandler { get; set; }

        bool IsLogTypeAllowed(LogType logType);
        void Log(LogType logType, object message);
    }
    public static class LoggerExtesions
    {
        public static void Log(this ILogger logger, object msg)
        {
            logger.Log(LogType.Log, msg);
        }
        public static void LogWarning(this ILogger logger, object msg)
        {
            logger.Log(LogType.Warning, msg);
        }
        public static void LogError(this ILogger logger, object msg)
        {
            logger.Log(LogType.Error, msg);
        }

        public static bool LogEnabled(this ILogger logger)
        {
            return logger.IsLogTypeAllowed(LogType.Log);
        }
        public static bool WarEnabled(this ILogger logger)
        {
            return logger.IsLogTypeAllowed(LogType.Warning);
        }
        public static bool ErrorEnabled(this ILogger logger)
        {
            return logger.IsLogTypeAllowed(LogType.Error);
        }
    }
}
