using System;
using System.IO;
using Serilog;
using Serilog.Events;

namespace Common.Logging
{
    public static class LoggingConfiguration
    {
        public static void Initialize(string componentName, string logFilePath = null)
        {
            var logConfig = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .Enrich.WithProperty("Component", componentName)
                .Enrich.FromLogContext()
                .WriteTo.Console(
                    outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{Component}] {Message:lj}{NewLine}{Exception}",
                    restrictedToMinimumLevel: LogEventLevel.Information);

            if (!string.IsNullOrEmpty(logFilePath))
            {
                var directory = Path.GetDirectoryName(logFilePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                logConfig.WriteTo.File(
                    logFilePath,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{Component}] {Message:lj}{NewLine}{Exception}",
                    rollingInterval: RollingInterval.Day,
                    restrictedToMinimumLevel: LogEventLevel.Debug);
            }

            Log.Logger = logConfig.CreateLogger();
        }

        public static void Close()
        {
            Log.CloseAndFlush();
        }
    }
} 