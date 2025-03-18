using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Common.Models;
using Common.Logging;
using System.Linq;

namespace Common.Networking
{
    // Client info class to track client connection details
    public class ClientInfo
    {
        public string ClientId { get; set; }
        public TcpClient Client { get; set; }
        public DateTime ConnectedAt { get; set; }
        public IPEndPoint RemoteEndPoint { get; set; }
        
        public ClientInfo(string clientId, TcpClient client)
        {
            ClientId = clientId;
            Client = client;
            ConnectedAt = DateTime.UtcNow;
            RemoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
        }
    }
    
    public abstract class SocketServer
    {
        protected readonly string _serverName;
        protected readonly int _port;
        protected TcpListener _listener;
        protected bool _isRunning;
        protected CancellationTokenSource _cancellationTokenSource;
        protected ConcurrentDictionary<string, ClientInfo> _clients = new ConcurrentDictionary<string, ClientInfo>();

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
            
            foreach (var clientInfo in _clients.Values)
            {
                clientInfo.Client?.Close();
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
                    var remoteEndPoint = client.Client.RemoteEndPoint as System.Net.IPEndPoint;
                    // Create a more descriptive client ID that includes connection info
                    var clientId = $"{Guid.NewGuid().ToString().Substring(0, 6)}_{remoteEndPoint?.Address}_{remoteEndPoint?.Port}";
                    
                    var clientInfo = new ClientInfo(clientId, client);
                    _clients.TryAdd(clientId, clientInfo);
                    
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
                // Make sure we have a ClientInfo for this client
                if (!_clients.TryGetValue(clientId, out var clientInfo))
                {
                    // Add it if it doesn't exist (shouldn't happen, but just in case)
                    clientInfo = new ClientInfo(clientId, client);
                    _clients.TryAdd(clientId, clientInfo);
                }
                
                using (client)
                {
                    var remoteEndPoint = client.Client.RemoteEndPoint as System.Net.IPEndPoint;
                    Logger.Connection(LogLevel.Info, $"Client {clientId} connected from {remoteEndPoint?.Address}:{remoteEndPoint?.Port}");
                    
                    var buffer = new byte[4096];
                    var stream = client.GetStream();
                    var messageBuilder = new StringBuilder();
                    
                    while (!cancellationToken.IsCancellationRequested && client.Connected)
                    {
                        try
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
                                        
                                        // Force a flush of the network stream after sending a response
                                        if (_clients.TryGetValue(clientId, out var currClientInfo) && currClientInfo?.Client?.GetStream() != null)
                                        {
                                            await currClientInfo.Client.GetStream().FlushAsync(cancellationToken);
                                        }
                                    }
                                    else
                                    {
                                        Logger.Error($"Received null message from client {clientId}: {messageJson}");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Logger.Error($"Error processing message from client {clientId}: {ex.Message}");
                                }
                            }
                            
                            // Keep any remaining partial message
                            var lastNewline = json.LastIndexOf('\n');
                            if (lastNewline >= 0)
                            {
                                messageBuilder.Remove(0, lastNewline + 1);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (IOException ex)
                        {
                            Logger.Error($"IO error for client {clientId}: {ex.Message}");
                            break;
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"Unexpected error for client {clientId}: {ex.Message}");
                            break;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException || ex is SocketException)
            {
                // Expected when client disconnects
            }
            catch (Exception ex)
            {
                Logger.Error($"Error handling client {clientId}: {ex.Message}");
            }
            finally
            {
                _clients.TryRemove(clientId, out _);
                Logger.Connection(LogLevel.Info, $"Client {clientId} disconnected");
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

            if (!_clients.TryGetValue(clientId, out var clientInfo))
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
                
                Logger.Connection(LogLevel.Debug, $"Sending message to {clientId.Substring(0, 6)}: Type={message.Type}");
                
                var stream = clientInfo.Client.GetStream();
                await stream.WriteAsync(messageBytes, 0, messageBytes.Length);
                
                // Force a flush to ensure the message is sent immediately
                await stream.FlushAsync();
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