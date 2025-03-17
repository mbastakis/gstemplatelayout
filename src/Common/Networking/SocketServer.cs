using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Common.Models;
using Common.Logging;

namespace Common.Networking
{
    public abstract class SocketServer
    {
        protected readonly string _serverName;
        protected readonly int _port;
        protected TcpListener _listener;
        protected bool _isRunning;
        protected CancellationTokenSource _cancellationTokenSource;
        protected ConcurrentDictionary<string, TcpClient> _clients = new ConcurrentDictionary<string, TcpClient>();

        public SocketServer(string serverName, int port)
        {
            _serverName = serverName;
            _port = port;
        }

        public virtual async Task Start()
        {
            if (_isRunning)
                return;

            _isRunning = true;
            _cancellationTokenSource = new CancellationTokenSource();
            
            _listener = new TcpListener(IPAddress.Any, _port);
            _listener.Start();
            
            Logger.System(LogLevel.Info, $"{_serverName} started on port {_port}");
            
            _ = AcceptClientsAsync(_cancellationTokenSource.Token);
        }

        public virtual void Stop()
        {
            if (!_isRunning)
                return;

            _isRunning = false;
            _cancellationTokenSource?.Cancel();
            
            foreach (var client in _clients.Values)
            {
                client.Close();
            }
            
            _clients.Clear();
            _listener.Stop();
            
            Logger.System(LogLevel.Info, $"{_serverName} stopped");
        }

        protected virtual async Task AcceptClientsAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    var clientId = Guid.NewGuid().ToString();
                    
                    _clients.TryAdd(clientId, client);
                    
                    _ = HandleClientAsync(clientId, client, cancellationToken);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException || ex is SocketException)
            {
                // Expected when stopping the server
            }
            catch (Exception ex)
            {
                Logger.Error($"Error accepting clients", ex);
            }
        }

        protected virtual async Task HandleClientAsync(string clientId, TcpClient client, CancellationToken cancellationToken)
        {
            try
            {
                using (client)
                {
                    var remoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
                    Logger.Connection(LogLevel.Info, $"Client {clientId.Substring(0, 6)} connected from {remoteEndPoint?.Address}:{remoteEndPoint?.Port}");
                    
                    var buffer = new byte[4096];
                    var stream = client.GetStream();
                    var messageBuilder = new StringBuilder();
                    
                    while (!cancellationToken.IsCancellationRequested && client.Connected)
                    {
                        var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                        
                        if (bytesRead == 0)
                            break;
                            
                        var data = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                        messageBuilder.Append(data);
                        
                        // Process complete messages
                        var json = messageBuilder.ToString();
                        var messages = json.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                        
                        foreach (var messageJson in messages)
                        {
                            if (string.IsNullOrWhiteSpace(messageJson))
                                continue;
                                
                            try
                            {
                                var message = Message.Deserialize(messageJson);
                                if (message != null)
                                {
                                    await ProcessMessageAsync(clientId, message);
                                }
                                else
                                {
                                    Logger.Error($"Received null message from client {clientId.Substring(0, 6)}: {messageJson}");
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Error($"Error processing message from client {clientId.Substring(0, 6)}", ex);
                            }
                        }
                        
                        // Keep any remaining partial message
                        var lastNewline = json.LastIndexOf('\n');
                        if (lastNewline >= 0)
                        {
                            messageBuilder.Remove(0, lastNewline + 1);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException || ex is SocketException)
            {
                // Expected when client disconnects
                Logger.Connection(LogLevel.Debug, $"Client {clientId.Substring(0, 6)} disconnected due to socket or cancellation: {ex.GetType().Name}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error handling client {clientId.Substring(0, 6)}", ex);
            }
            finally
            {
                _clients.TryRemove(clientId, out _);
                Logger.Connection(LogLevel.Info, $"Client {clientId.Substring(0, 6)} disconnected");
                await OnClientDisconnectedAsync(clientId);
            }
        }

        protected virtual Task OnClientDisconnectedAsync(string clientId)
        {
            return Task.CompletedTask;
        }

        protected abstract Task ProcessMessageAsync(string clientId, Message message);

        public async Task SendMessageAsync(string clientId, Message message)
        {
            if (message == null)
            {
                Logger.Error($"Attempted to send null message to client {clientId.Substring(0, 6)}");
                return;
            }

            if (!_clients.TryGetValue(clientId, out var client))
            {
                Logger.Connection(LogLevel.Warning, $"Client {clientId.Substring(0, 6)} not found for sending message");
                return;
            }
                
            try
            {
                var messageJson = Message.Serialize(message);
                if (string.IsNullOrEmpty(messageJson))
                {
                    Logger.Error($"Message serialization produced null or empty string for client {clientId.Substring(0, 6)}");
                    return;
                }
                
                // Add message delimiter
                messageJson += "\n";
                var messageBytes = Encoding.UTF8.GetBytes(messageJson);
                
                var stream = client.GetStream();
                await stream.WriteAsync(messageBytes, 0, messageBytes.Length);
            }
            catch (Exception ex)
            {
                Logger.Error($"Error sending message to client {clientId.Substring(0, 6)}", ex);
                _clients.TryRemove(clientId, out _);
            }
        }

        public async Task BroadcastMessageAsync(Message message)
        {
            if (message == null)
            {
                Logger.Error("Attempted to broadcast null message");
                return;
            }

            foreach (var clientId in _clients.Keys)
            {
                await SendMessageAsync(clientId, message);
            }
        }

        public async Task BroadcastMessageAsync(Message message, string excludeClientId)
        {
            if (message == null)
            {
                Logger.Error("Attempted to broadcast null message");
                return;
            }

            foreach (var clientId in _clients.Keys)
            {
                if (clientId != excludeClientId)
                {
                    await SendMessageAsync(clientId, message);
                }
            }
        }
    }
} 