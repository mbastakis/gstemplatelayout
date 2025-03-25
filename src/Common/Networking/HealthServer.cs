using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Common.Logging;

namespace Common.Networking
{
    /// <summary>
    /// Provides HTTP health check endpoints for services
    /// </summary>
    public class HealthServer : IDisposable
    {
        private readonly string _serviceName;
        private readonly int _port;
        private HttpListener _listener;
        private CancellationTokenSource _cts;
        private bool _isDisposed = false;
        
        /// <summary>
        /// Gets or sets whether the service is ready to accept traffic
        /// </summary>
        public bool IsReady { get; set; } = false;
        
        /// <summary>
        /// Gets or sets whether the service is running
        /// </summary>
        public bool IsRunning { get; set; } = false;
        
        /// <summary>
        /// Function that provides additional status information for the /status endpoint
        /// </summary>
        public Func<string> GetAdditionalStatus { get; set; }

        /// <summary>
        /// Creates a new health server
        /// </summary>
        /// <param name="serviceName">Name of the service being monitored</param>
        /// <param name="port">Port to listen on</param>
        public HealthServer(string serviceName, int port)
        {
            _serviceName = serviceName;
            _port = port;
        }

        /// <summary>
        /// Starts the health server
        /// </summary>
        public void Start()
        {
            try
            {
                _cts = new CancellationTokenSource();
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://+:{_port}/");
                _listener.Start();
                
                IsRunning = true;
                
                Logger.System(LogLevel.Info, $"Health check server for {_serviceName} started on port {_port}");
                
                // Handle health check requests
                Task.Run(() => HandleHealthRequests(_cts.Token));
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to start health check server for {_serviceName}: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Stops the health server
        /// </summary>
        public void Stop()
        {
            IsRunning = false;
            IsReady = false;
            _cts?.Cancel();
            _listener?.Stop();
            
            Logger.System(LogLevel.Info, $"Health check server for {_serviceName} stopped");
        }
        
        private async Task HandleHealthRequests(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested && _listener.IsListening)
                {
                    var context = await _listener.GetContextAsync();
                    
                    // Handle request in a separate task to keep accepting new requests
                    _ = Task.Run(() => 
                    {
                        try
                        {
                            var response = context.Response;
                            string responseString;
                            
                            switch (context.Request.Url.AbsolutePath)
                            {
                                case "/health":
                                    // Liveness check - just return OK if the server is running
                                    if (IsRunning)
                                    {
                                        response.StatusCode = 200;
                                        responseString = "OK";
                                    }
                                    else
                                    {
                                        response.StatusCode = 503;
                                        responseString = "Service not running";
                                    }
                                    break;
                                    
                                case "/ready":
                                    // Readiness check - only return OK if the server is fully initialized
                                    if (IsReady)
                                    {
                                        response.StatusCode = 200;
                                        responseString = "Ready";
                                    }
                                    else
                                    {
                                        response.StatusCode = 503;
                                        responseString = "Not Ready";
                                    }
                                    break;
                                    
                                case "/status":
                                    // More detailed status for debugging
                                    response.StatusCode = 200;
                                    responseString = $"{_serviceName} Status:\n" +
                                        $"Running: {IsRunning}\n" +
                                        $"Ready: {IsReady}\n";
                                        
                                    // Add additional status info if available
                                    if (GetAdditionalStatus != null)
                                    {
                                        responseString += GetAdditionalStatus();
                                    }
                                    break;
                                    
                                default:
                                    response.StatusCode = 404;
                                    responseString = "Not Found";
                                    break;
                            }
                            
                            byte[] buffer = Encoding.UTF8.GetBytes(responseString);
                            response.ContentLength64 = buffer.Length;
                            response.ContentType = "text/plain";
                            
                            response.OutputStream.Write(buffer, 0, buffer.Length);
                            response.OutputStream.Close();
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"Error handling health request: {ex.Message}", ex);
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in health check server: {ex.Message}", ex);
            }
        }

        public void Dispose()
        {
            if (!_isDisposed)
            {
                Stop();
                _cts?.Dispose();
                _isDisposed = true;
            }
        }
    }
} 