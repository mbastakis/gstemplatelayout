using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Common.Models;
using Common.Networking;
using System.Net.Sockets;
using System.Text;
using Common.Logging;
using System.Text.Json;
using System.Net;
using System.Collections.Generic;

namespace MasterServer
{
    public class MasterServer : SocketServer
    {
        private readonly ConcurrentDictionary<string, GameServerInfo> _gameServers = new ConcurrentDictionary<string, GameServerInfo>();
        private readonly ConcurrentDictionary<string, string> _clientToGameServerMap = new ConcurrentDictionary<string, string>();
        private Timer _heartbeatCheckTimer;
        private readonly TimeSpan _heartbeatTimeout = TimeSpan.FromSeconds(30);
        private string _lastGameServerStatus = string.Empty;
        private HealthServer _healthServer;

        public MasterServer(int port) : base("MasterServer", port)
        {
            Logger.Initialize("MasterServer", "logs/master-server.log");
        }

        public override async Task Start()
        {
            Logger.System(LogLevel.Info, "Starting Master Server...");
            await base.Start();
            
            _heartbeatCheckTimer = new Timer(CheckGameServerHeartbeats, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
            
            // Start health check server
            StartHealthServer();
            
            // After everything is initialized, mark as ready
            _healthServer.IsReady = true;
            Logger.System(LogLevel.Info, "Master Server is ready to accept connections");
        }

        public override void Stop()
        {
            _heartbeatCheckTimer?.Dispose();
            base.Stop();
            
            // Stop health server
            _healthServer?.Stop();
            _healthServer?.Dispose();
            
            Logger.System(LogLevel.Info, "Master Server stopped");
        }
        
        private void StartHealthServer()
        {
            try
            {
                _healthServer = new HealthServer("MasterServer", 8080);
                _healthServer.IsRunning = true;
                
                // Set additional status information
                _healthServer.GetAdditionalStatus = () => 
                    $"Connected Clients: {_clients.Count}\n" +
                    $"Registered Game Servers: {_gameServers.Count}";
                
                _healthServer.Start();
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to start health check server: {ex.Message}", ex);
            }
        }

        private void CheckGameServerHeartbeats(object state)
        {
            var now = DateTime.UtcNow;
            var deadServers = _gameServers.Values
                .Where(server => now - server.LastHeartbeat > _heartbeatTimeout)
                .ToList();
                
            foreach (var server in deadServers)
            {
                var logProps = new Dictionary<string, object>
                {
                    { "ServerId", server.Id },
                    { "ShortServerId", server.Id.Substring(0, Math.Min(6, server.Id.Length)) },
                    { "Endpoint", server.Endpoint },
                    { "LastHeartbeat", server.LastHeartbeat }
                };
                
                Logger.Connection(LogLevel.Warning, "Game server {ShortServerId} timed out", logProps);
                _gameServers.TryRemove(server.Id, out _);
            }
            
            // Only log if there's a change in the game server status
            var gameServerStatus = string.Join(", ", _gameServers.Values.Select(s => 
                $"{s.Id.Substring(0, Math.Min(6, s.Id.Length))}: {s.Endpoint}, Players: {s.CurrentPlayers}/{s.MaxPlayers}"
            ));
            
            if (_lastGameServerStatus != gameServerStatus)
            {
                var logProps = new Dictionary<string, object>
                {
                    { "ServerCount", _gameServers.Count },
                    { "ServerDetails", _gameServers.Values.Select(s => new 
                        {
                            ServerId = s.Id.Substring(0, Math.Min(6, s.Id.Length)),
                            Endpoint = s.Endpoint,
                            CurrentPlayers = s.CurrentPlayers,
                            MaxPlayers = s.MaxPlayers
                        }).ToArray()
                    }
                };
                
                Logger.GameState(LogLevel.Info, "Active game servers: {ServerCount}", logProps);
                _lastGameServerStatus = gameServerStatus;
            }
        }

        protected override async Task ProcessMessageAsync(string clientId, Message message)
        {
            // Generate a correlation ID for this request
            var correlationId = Logger.NewCorrelationId();
            
            // Create structured logging context
            var logProps = new Dictionary<string, object>
            {
                { "ClientId", clientId },
                { "ShortenedClientId", clientId.Substring(0, Math.Min(6, clientId.Length)) },
                { "MessageType", message.Type.ToString() },
                { "CorrelationId", correlationId }
            };
            
            Logger.System(LogLevel.Info, "Processing message from {ShortenedClientId} of type {MessageType}", logProps);
            
            if (message.Type != MessageType.GameServerHeartbeat)
            {
                Logger.System(LogLevel.Debug, $"Received message from {clientId}: {message.Type}", logProps);
            }
            
            switch (message.Type)
            {
                case MessageType.RegisterGameServer:
                    await HandleGameServerRegistrationAsync(clientId, message);
                    break;
                    
                case MessageType.GameServerHeartbeat:
                    await HandleGameServerHeartbeatAsync(clientId, message);
                    break;
                    
                case MessageType.ClientConnect:
                    await HandleClientConnectAsync(clientId, message);
                    break;
                    
                case MessageType.ClientDisconnect:
                    await HandleClientDisconnectAsync(clientId);
                    break;
                    
                default:
                    Logger.System(LogLevel.Warning, "Unhandled message type: {MessageType}", logProps);
                    break;
            }
        }

        private async Task HandleGameServerRegistrationAsync(string clientId, Message message)
        {
            // Create structured logging context reusing current correlation ID
            var logProps = new Dictionary<string, object>
            {
                { "ClientId", clientId },
                { "ShortenedClientId", clientId.Substring(0, Math.Min(6, clientId.Length)) },
                { "MessageType", "RegisterGameServer" },
                { "CorrelationId", Logger.CorrelationId }
            };
            
            Logger.Connection(LogLevel.Info, "Processing game server registration from {ShortenedClientId}", logProps);
            var registrationData = message.GetData<GameServerRegistrationData>();
            
            if (registrationData == null)
            {
                logProps["Error"] = "InvalidRegistrationData";
                Logger.Error("Invalid game server registration data from {ShortenedClientId}", null, logProps);
                var failResponse = new GameServerRegistrationResponse
                {
                    Success = false,
                    Error = "Invalid registration data"
                };
                await SendMessageAsync(clientId, Message.Create<GameServerRegistrationResponse>(MessageType.RegisterGameServer, failResponse));
                return;
            }
            
            // Add registration data to log properties
            logProps["ServerId"] = registrationData.ServerId;
            logProps["ShortServerId"] = registrationData.ServerId.Substring(0, Math.Min(6, registrationData.ServerId.Length));
            logProps["Endpoint"] = registrationData.Endpoint;
            logProps["MaxPlayers"] = registrationData.MaxPlayers;
            
            Logger.Connection(LogLevel.Debug, "Registration data received: ServerId={ShortServerId}, Endpoint={Endpoint}, MaxPlayers={MaxPlayers}", logProps);
            
            var gameServer = new GameServerInfo
            {
                Id = registrationData.ServerId,
                Endpoint = registrationData.Endpoint,
                MaxPlayers = registrationData.MaxPlayers,
                LastHeartbeat = DateTime.UtcNow
            };
            
            _gameServers[gameServer.Id] = gameServer;
            
            // Add result to log properties
            logProps["RegisteredServers"] = _gameServers.Count;
            
            Logger.Connection(LogLevel.Info, "Game server registered: {ShortServerId} at {Endpoint}", logProps);
            
            // Acknowledge registration
            var response = new GameServerRegistrationResponse
            {
                Success = true
            };
            await SendMessageAsync(clientId, Message.Create<GameServerRegistrationResponse>(MessageType.RegisterGameServer, response));
            Logger.Connection(LogLevel.Debug, "Registration acknowledgment sent to {ShortenedClientId}", logProps);
        }

        private Task HandleGameServerHeartbeatAsync(string clientId, Message message)
        {
            var heartbeatData = message.GetData<GameServerHeartbeatData>();
            
            if (heartbeatData == null || !_gameServers.TryGetValue(heartbeatData.ServerId, out var gameServer))
            {
                var logProps = new Dictionary<string, object>
                {
                    { "ClientId", clientId },
                    { "ShortenedClientId", clientId.Substring(0, Math.Min(6, clientId.Length)) },
                    { "MessageType", "GameServerHeartbeat" },
                    { "Error", "InvalidHeartbeatData" }
                };
                
                Logger.Error("Invalid game server heartbeat data or unknown server from {ShortenedClientId}", null, logProps);
                return Task.CompletedTask;
            }
            
            // Only log changes in player count
            if (gameServer.CurrentPlayers != heartbeatData.CurrentPlayers)
            {
                var logProps = new Dictionary<string, object>
                {
                    { "ServerId", heartbeatData.ServerId },
                    { "ShortServerId", heartbeatData.ServerId.Substring(0, Math.Min(6, heartbeatData.ServerId.Length)) },
                    { "OldPlayerCount", gameServer.CurrentPlayers },
                    { "NewPlayerCount", heartbeatData.CurrentPlayers },
                    { "MaxPlayers", gameServer.MaxPlayers }
                };
                
                Logger.GameState(LogLevel.Info, "Server {ShortServerId} player count changed: {OldPlayerCount} -> {NewPlayerCount}", logProps);
            }
            
            gameServer.CurrentPlayers = heartbeatData.CurrentPlayers;
            gameServer.LastHeartbeat = DateTime.UtcNow;
            
            return Task.CompletedTask;
        }

        private async Task HandleClientConnectAsync(string clientId, Message message)
        {
            // Create structured log properties
            var logProps = new Dictionary<string, object>
            {
                { "ClientId", clientId },
                { "ShortenedClientId", clientId.Substring(0, Math.Min(6, clientId.Length)) },
                { "MessageType", "ClientConnect" },
                { "CorrelationId", Logger.CorrelationId }
            };
            
            Logger.Connection(LogLevel.Info, "Processing client connection request from {ShortenedClientId}", logProps);
            
            var connectData = message.GetData<ClientConnectData>();
            if (connectData == null)
            {
                logProps["Error"] = "InvalidConnectData";
                Logger.Error("Invalid client connect data from {ShortenedClientId}", null, logProps);
                
                var response = new ClientConnectResponse
                {
                    Success = false,
                    Error = "Invalid connect data"
                };
                await SendMessageAsync(clientId, Message.Create<ClientConnectResponse>(MessageType.ClientConnect, response));
                return;
            }
            
            // Get list of available game servers
            var availableServers = _gameServers.Values
                .Where(server => server.CurrentPlayers < server.MaxPlayers)
                .OrderBy(server => server.CurrentPlayers) // Load balancing - prioritize emptier servers
                .ToList();
                
            logProps["AvailableServerCount"] = availableServers.Count;
            
            if (!availableServers.Any())
            {
                logProps["Error"] = "NoAvailableServers";
                Logger.Connection(LogLevel.Warning, "No available game servers for client {ShortenedClientId}", logProps);
                
                var response = new ClientConnectResponse
                {
                    Success = false,
                    Error = "No available game servers"
                };
                await SendMessageAsync(clientId, Message.Create<ClientConnectResponse>(MessageType.ClientConnect, response));
                return;
            }
            
            // Assign client to first available server
            var selectedServer = availableServers.First();
            
            logProps["ServerId"] = selectedServer.Id;
            logProps["ShortServerId"] = selectedServer.Id.Substring(0, Math.Min(6, selectedServer.Id.Length));
            logProps["ServerEndpoint"] = selectedServer.Endpoint;
            logProps["ServerCurrentPlayers"] = selectedServer.CurrentPlayers;
            logProps["ServerMaxPlayers"] = selectedServer.MaxPlayers;
            
            _clientToGameServerMap[clientId] = selectedServer.Id;
            
            Logger.Connection(LogLevel.Info, "Client {ShortenedClientId} assigned to game server {ShortServerId}", logProps);
            
            // Respond with server connection details
            var connectResponse = new ClientConnectResponse
            {
                Success = true,
                ServerId = selectedServer.Id,
                ServerEndpoint = selectedServer.Endpoint
            };
            
            await SendMessageAsync(clientId, Message.Create<ClientConnectResponse>(MessageType.ClientConnect, connectResponse));
            Logger.Connection(LogLevel.Debug, "Connection details sent to client {ShortenedClientId}", logProps);
        }

        private Task HandleClientDisconnectAsync(string clientId)
        {
            var logProps = new Dictionary<string, object>
            {
                { "ClientId", clientId },
                { "ShortenedClientId", clientId.Substring(0, Math.Min(6, clientId.Length)) }
            };
            
            if (_clientToGameServerMap.TryRemove(clientId, out var serverId))
            {
                logProps["ServerId"] = serverId;
                logProps["ShortServerId"] = serverId.Substring(0, Math.Min(6, serverId.Length));
                
                Logger.Connection(LogLevel.Info, "Client {ShortenedClientId} disconnected from server {ShortServerId}", logProps);
            }
            else
            {
                Logger.Connection(LogLevel.Info, "Client {ShortenedClientId} disconnected (not assigned to a server)", logProps);
            }
            
            return Task.CompletedTask;
        }

        protected override Task OnClientDisconnectedAsync(string clientId)
        {
            return HandleClientDisconnectAsync(clientId);
        }

        protected virtual async Task HandleClientAsync(string clientId, TcpClient client, CancellationToken cancellationToken)
        {
            try
            {
                using (client)
                {
                    var remoteEndPoint = client.Client.RemoteEndPoint as System.Net.IPEndPoint;
                    var isLikelyHealthCheck = false;
                    
                    // Check if there was no data received within a short time - likely a health check
                    using var healthCheckTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                    var combinedToken = CancellationTokenSource.CreateLinkedTokenSource(
                        healthCheckTimeout.Token, cancellationToken).Token;
                    
                    Logger.Connection(LogLevel.Info, $"Client {clientId} connected from {remoteEndPoint?.Address}:{remoteEndPoint?.Port}");
                    
                    var buffer = new byte[4096];
                    var stream = client.GetStream();
                    var messageBuilder = new StringBuilder();
                    
                    try
                    {
                        // Try to read data with a timeout
                        var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, combinedToken);
                        
                        if (bytesRead == 0)
                        {
                            Logger.Connection(LogLevel.Debug, $"Client {clientId} connected but sent no data - likely a health check");
                            isLikelyHealthCheck = true;
                            return;
                        }
                            
                        var data = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                        messageBuilder.Append(data);
                    }
                    catch (OperationCanceledException)
                    {
                        // No data received within timeout - probably a health check
                        if (healthCheckTimeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                        {
                            Logger.Connection(LogLevel.Debug, $"Client {clientId} timed out without sending data - likely a health check");
                            isLikelyHealthCheck = true;
                            return;
                        }
                        throw; // Re-throw if it's not a health check timeout
                    }
                    
                    // Continue with normal message processing if we got past the health check
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
                                    
                                    // Force a flush of the network stream after sending a response
                                    if (_clients.TryGetValue(clientId, out var clientInfo) && clientInfo?.Client?.GetStream() != null)
                                    {
                                        await clientInfo.Client.GetStream().FlushAsync(cancellationToken);
                                    }
                                }
                                else
                                {
                                    Logger.Error($"Received null message from client {clientId}: {messageJson}");
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Error($"Error processing message from client {clientId}", ex);
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
                Logger.Connection(LogLevel.Debug, $"Client {clientId} disconnected due to socket or cancellation: {ex.GetType().Name}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error handling client {clientId}", ex);
            }
            finally
            {
                _clients.TryRemove(clientId, out _);
                Logger.Connection(LogLevel.Info, $"Client {clientId} disconnected");
                await OnClientDisconnectedAsync(clientId);
            }
        }
    }
} 