using System;
using System.Collections.Generic;
using System.IO;
using Serilog;
using Serilog.Events;

namespace Common.Logging
{
    public enum LogCategory
    {
        Connection,
        GameState,
        PlayerAction,
        System,
        Error
    }

    public enum LogLevel
    {
        Debug,
        Info,
        Warning,
        Error
    }

    public static class Logger
    {
        private static readonly Dictionary<LogCategory, bool> _enabledCategories = new Dictionary<LogCategory, bool>
        {
            { LogCategory.Connection, true },
            { LogCategory.GameState, true },
            { LogCategory.PlayerAction, true },
            { LogCategory.System, true },
            { LogCategory.Error, true }
        };

        public static void Initialize(string componentName, string logFilePath = null)
        {
            LoggingConfiguration.Initialize(componentName, logFilePath);
        }

        public static void SetCategoryEnabled(LogCategory category, bool enabled)
        {
            _enabledCategories[category] = enabled;
        }

        public static void Write(LogCategory category, LogLevel level, string message)
        {
            if (!_enabledCategories.TryGetValue(category, out var enabled) || !enabled)
                return;

            var logLevel = ConvertToSerilogLevel(level);
            var logMessage = $"[{category}] {message}";

            switch (level)
            {
                case LogLevel.Debug:
                    Log.Debug(logMessage);
                    break;
                case LogLevel.Info:
                    Log.Information(logMessage);
                    break;
                case LogLevel.Warning:
                    Log.Warning(logMessage);
                    break;
                case LogLevel.Error:
                    Log.Error(logMessage);
                    break;
            }
        }

        public static void Connection(LogLevel level, string message)
        {
            Write(LogCategory.Connection, level, message);
        }

        public static void GameState(LogLevel level, string message)
        {
            Write(LogCategory.GameState, level, message);
        }

        public static void PlayerAction(LogLevel level, string message)
        {
            Write(LogCategory.PlayerAction, level, message);
        }

        public static void System(LogLevel level, string message)
        {
            Write(LogCategory.System, level, message);
        }

        public static void Error(string message, Exception exception = null)
        {
            if (exception != null)
            {
                Log.Error(exception, $"[Error] {message}");
            }
            else
            {
                Log.Error($"[Error] {message}");
            }
        }

        public static void Close()
        {
            LoggingConfiguration.Close();
        }

        private static LogEventLevel ConvertToSerilogLevel(LogLevel level)
        {
            return level switch
            {
                LogLevel.Debug => LogEventLevel.Debug,
                LogLevel.Info => LogEventLevel.Information,
                LogLevel.Warning => LogEventLevel.Warning,
                LogLevel.Error => LogEventLevel.Error,
                _ => LogEventLevel.Information
            };
        }
    }
} 