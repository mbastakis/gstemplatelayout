using System;
using System.IO;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Json;
using Serilog.Formatting.Compact;
using Serilog.Configuration;

namespace Common.Logging
{
    public static class LoggingConfiguration
    {
        public static void Initialize(string componentName, string logFilePath = null)
        {
            // Detect if we're running in a container environment (likely Kubernetes)
            bool isContainerEnvironment = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST") != null;
            bool usePlainConsoleLogging = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("USE_PLAIN_CONSOLE_LOGGING"));
            
            var loggerConfiguration = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .Enrich.WithProperty("Component", componentName)
                .Enrich.WithProperty("Application", componentName);
                
            if (isContainerEnvironment)
            {
                loggerConfiguration.Enrich.FromLogContext();
                
                // Check if we should use plain text logging for console (to avoid nested JSON)
                if (usePlainConsoleLogging)
                {
                    // Use plain text logging to avoid the nested JSON problem
                    loggerConfiguration.WriteTo.Console(
                        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] [{Category}] {Message:lj}{NewLine}{Exception}");
                }
                else
                {
                    // Use JSON formatting - but be aware this may cause nested JSON issues with container logs
                    loggerConfiguration.WriteTo.Console(new CompactJsonFormatter());
                }
                
                // Still keep a JSON file log for potential debugging if needed
                try
                {
                    if (Directory.Exists("/logs"))
                    {
                        loggerConfiguration.WriteTo.File(
                            formatter: new CompactJsonFormatter(),
                            path: "/logs/app.log",
                            rollingInterval: RollingInterval.Day,
                            fileSizeLimitBytes: 10 * 1024 * 1024);
                    }
                }
                catch (Exception ex)
                {
                    // Just log to console if file logging fails
                    Console.WriteLine($"Warning: Could not set up file logging: {ex.Message}");
                }
            }
            else
            {
                // In development environments, use a readable format for console
                loggerConfiguration
                    .Enrich.FromLogContext()
                    .WriteTo.Console(
                        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{Category}] {Message:lj}{NewLine}{Properties}{NewLine}{Exception}",
                        restrictedToMinimumLevel: LogEventLevel.Information);
                        
                // Only use file logging outside of containers
                if (!string.IsNullOrEmpty(logFilePath))
                {
                    var directory = Path.GetDirectoryName(logFilePath);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    // File logging with CompactJson format - better for structured logs
                    loggerConfiguration.WriteTo.File(
                        formatter: new CompactJsonFormatter(),
                        path: logFilePath,
                        rollingInterval: RollingInterval.Day,
                        fileSizeLimitBytes: 5 * 1024 * 1024,
                        retainedFileCountLimit: 10,
                        restrictedToMinimumLevel: LogEventLevel.Debug);
                }
            }

            Log.Logger = loggerConfiguration.CreateLogger();
            
            // Log startup message with environment information
            var loggingMode = isContainerEnvironment ? 
                (usePlainConsoleLogging ? "container with plain text logging" : "container with JSON logging") : 
                "development";
                
            Log.Information("Logging initialized for {Component} in {Environment} environment",
                componentName, loggingMode);
        }

        public static void Close()
        {
            Log.CloseAndFlush();
        }
    }
} 