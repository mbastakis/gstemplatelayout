using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Serilog;
using Serilog.Context;

namespace Common.Logging
{
    public enum LogLevel
    {
        Debug,
        Info,
        Warning,
        Error
    }

    public static class Logger
    {
        private static readonly AsyncLocal<string> _correlationId = new AsyncLocal<string>();
        
        /// <summary>
        /// Gets the current correlation ID for request tracing
        /// </summary>
        public static string CorrelationId => _correlationId.Value;
        
        /// <summary>
        /// Creates a new correlation ID for request tracing
        /// </summary>
        /// <returns>The new correlation ID</returns>
        public static string NewCorrelationId()
        {
            _correlationId.Value = Guid.NewGuid().ToString();
            return _correlationId.Value;
        }
        
        /// <summary>
        /// Sets the correlation ID for the current context
        /// </summary>
        public static void SetCorrelationId(string correlationId)
        {
            if (!string.IsNullOrEmpty(correlationId))
            {
                _correlationId.Value = correlationId;
            }
        }
        
        /// <summary>
        /// Initialize the logger with the component name and log file path
        /// </summary>
        public static void Initialize(string component, string logFilePath = null)
        {
            LoggingConfiguration.Initialize(component, logFilePath);
        }

        /// <summary>
        /// Log a connection-related message
        /// </summary>
        public static void Connection(LogLevel level, string messageTemplate, Dictionary<string, object> properties = null)
        {
            var props = properties ?? new Dictionary<string, object>();
            props["Category"] = "Connection";
            
            WriteStructured(level, messageTemplate, props);
        }

        /// <summary>
        /// Log a game state-related message
        /// </summary>
        public static void GameState(LogLevel level, string messageTemplate, Dictionary<string, object> properties = null)
        {
            var props = properties ?? new Dictionary<string, object>();
            props["Category"] = "GameState";
            
            WriteStructured(level, messageTemplate, props);
        }

        /// <summary>
        /// Log a player action-related message
        /// </summary>
        public static void PlayerAction(LogLevel level, string messageTemplate, Dictionary<string, object> properties = null)
        {
            var props = properties ?? new Dictionary<string, object>();
            props["Category"] = "PlayerAction";
            
            WriteStructured(level, messageTemplate, props);
        }

        /// <summary>
        /// Log a system-related message
        /// </summary>
        public static void System(LogLevel level, string messageTemplate, Dictionary<string, object> properties = null)
        {
            var props = properties ?? new Dictionary<string, object>();
            props["Category"] = "System";
            
            WriteStructured(level, messageTemplate, props);
        }

        /// <summary>
        /// Log an error message
        /// </summary>
        public static void Error(string messageTemplate, Exception ex = null, Dictionary<string, object> properties = null)
        {
            var props = properties ?? new Dictionary<string, object>();
            props["Category"] = "Error";
            
            if (ex != null)
            {
                props["ExceptionType"] = ex.GetType().Name;
                props["ExceptionMessage"] = ex.Message;
                props["StackTrace"] = ex.StackTrace;
            }
            
            WriteStructured(LogLevel.Error, messageTemplate, props, ex);
        }
        
        /// <summary>
        /// Log a structured message for a client connection
        /// </summary>
        public static void ClientConnection(string clientId, string action, Dictionary<string, object> additionalData = null)
        {
            var props = additionalData ?? new Dictionary<string, object>();
            props["ClientId"] = clientId;
            props["ShortenedClientId"] = clientId.Substring(0, Math.Min(6, clientId.Length));
            props["Action"] = action;
            props["Category"] = "Connection";
            
            WriteStructured(
                LogLevel.Info, 
                "Client {ShortenedClientId} {Action}", 
                props
            );
        }
        
        /// <summary>
        /// Log a structured message for a game event
        /// </summary>
        public static void GameEvent(string eventType, Dictionary<string, object> additionalData = null)
        {
            var props = additionalData ?? new Dictionary<string, object>();
            props["EventType"] = eventType;
            props["Category"] = "GameState";
            
            WriteStructured(
                LogLevel.Info, 
                "Game event: {EventType}", 
                props
            );
        }
        
        /// <summary>
        /// Close and flush the logger
        /// </summary>
        public static void Close()
        {
            Log.CloseAndFlush();
        }
        
        /// <summary>
        /// Writes a structured log message
        /// </summary>
        private static void WriteStructured(
            LogLevel level, 
            string messageTemplate, 
            Dictionary<string, object> properties, 
            Exception exception = null)
        {
            // Ensure properties is not null
            var props = properties ?? new Dictionary<string, object>();
            
            // Add correlation ID if it exists and isn't already in properties
            if (!string.IsNullOrEmpty(_correlationId.Value) && !props.ContainsKey("CorrelationId"))
            {
                props["CorrelationId"] = _correlationId.Value;
            }
            
            // Create an array of property values from the properties dictionary
            // for structured parameters in the messageTemplate
            var propertyValues = new List<object>();
            
            // This helper extracts named placeholders from the messageTemplate
            // So {ClientId} in the message will be populated from props["ClientId"]
            string preprocessedTemplate = PreprocessMessageTemplate(messageTemplate, props, propertyValues);
            
            // Push all properties to LogContext for structured context
            using (PushProperties(props))
            {
                switch (level)
                {
                    case LogLevel.Debug:
                        Log.Debug(exception, preprocessedTemplate, propertyValues.ToArray());
                        break;
                    case LogLevel.Info:
                        Log.Information(exception, preprocessedTemplate, propertyValues.ToArray());
                        break;
                    case LogLevel.Warning:
                        Log.Warning(exception, preprocessedTemplate, propertyValues.ToArray());
                        break;
                    case LogLevel.Error:
                        Log.Error(exception, preprocessedTemplate, propertyValues.ToArray());
                        break;
                    default:
                        Log.Information(exception, preprocessedTemplate, propertyValues.ToArray());
                        break;
                }
            }
        }
        
        /// <summary>
        /// Preprocesses a message template to ensure all named placeholders have corresponding values
        /// </summary>
        private static string PreprocessMessageTemplate(
            string messageTemplate, 
            Dictionary<string, object> properties, 
            List<object> propertyValues)
        {
            // If the message doesn't contain any placeholders in the form {Name},
            // just return it as is
            if (!messageTemplate.Contains("{"))
            {
                return messageTemplate;
            }
            
            // Simple parser for the Serilog message template format
            // Extracts placeholders like {Name} and ensures values are available
            var templateParts = messageTemplate.Split('{');
            for (int i = 1; i < templateParts.Length; i++)
            {
                var part = templateParts[i];
                var closeBraceIndex = part.IndexOf('}');
                if (closeBraceIndex >= 0)
                {
                    var propertyName = part.Substring(0, closeBraceIndex).Trim();
                    if (!string.IsNullOrEmpty(propertyName) && properties.TryGetValue(propertyName, out var value))
                    {
                        propertyValues.Add(value);
                    }
                    else
                    {
                        // If property not found, add a placeholder value
                        propertyValues.Add($"[{propertyName}]");
                    }
                }
            }
            
            return messageTemplate;
        }
        
        // Helper for pushing multiple properties in one call
        private static IDisposable PushProperties(Dictionary<string, object> properties)
        {
            if (properties == null || properties.Count == 0)
                return new NoopDisposable();
                
            // Create a composite disposable for all properties
            var disposables = new CompositeDisposable();
            
            foreach (var property in properties)
            {
                if (property.Value != null)
                {
                    disposables.Add(LogContext.PushProperty(property.Key, property.Value));
                }
            }
            
            return disposables;
        }
        
        // Simple composite disposable implementation
        private class CompositeDisposable : IDisposable
        {
            private readonly List<IDisposable> _disposables = new List<IDisposable>();
            
            public void Add(IDisposable disposable)
            {
                _disposables.Add(disposable);
            }
            
            public void Dispose()
            {
                foreach (var disposable in _disposables)
                {
                    disposable.Dispose();
                }
                _disposables.Clear();
            }
        }
        
        // No-op disposable for empty case
        private class NoopDisposable : IDisposable
        {
            public void Dispose() {}
        }
    }
} 