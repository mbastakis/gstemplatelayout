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

namespace MasterServer
{
    public class MasterServer : SocketServer
    {
        private readonly ConcurrentDictionary<string, GameServerInfo> _gameServers = new ConcurrentDictionary<string, GameServerInfo>();
        private readonly ConcurrentDictionary<string, string> _clientToGameServerMap = new ConcurrentDictionary<string, string>();
        private Timer _heartbeatCheckTimer;
        private readonly TimeSpan _heartbeatTimeout = TimeSpan.FromSeconds(30);
        private string _lastGameServerStatus = string.Empty;

        public MasterServer(int port) : base("MasterServer", port)
        {
            Logger.Initialize("MasterServer", "logs/master-server.log");
        }

        public override async Task Start()
        {
            Logger.System(LogLevel.Info, "Starting Master Server...");
            await base.Start();
            
            _heartbeatCheckTimer = new Timer(CheckGameServerHeartbeats, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        }

        public override void Stop()
        {
            _heartbeatCheckTimer?.Dispose();
            base.Stop();
            Logger.System(LogLevel.Info, "Master Server stopped");
        }

        private void CheckGameServerHeartbeats(object state)
        {
            var now = DateTime.UtcNow;
            var deadServers = _gameServers.Values
                .Where(server => now - server.LastHeartbeat > _heartbeatTimeout)
                .ToList();
                
            foreach (var server in deadServers)
            {
                Logger.Connection(LogLevel.Warning, $"Game server {server.Id} timed out");
                _gameServers.TryRemove(server.Id, out _);
            }
            
            // Only log if there's a change in the game server status
            var gameServerStatus = string.Join(", ", _gameServers.Values.Select(s => 
                $"{s.Id.Substring(0, 6)}: {s.Endpoint}, Players: {s.CurrentPlayers}/{s.MaxPlayers}"
            ));
            
            if (_lastGameServerStatus != gameServerStatus)
            {
                Logger.GameState(LogLevel.Info, $"Active game servers: {_gameServers.Count}");
                foreach (var server in _gameServers.Values)
                {
                    Logger.GameState(LogLevel.Info, $"  - {server.Id.Substring(0, 6)}: {server.Endpoint}, Players: {server.CurrentPlayers}/{server.MaxPlayers}");
                }
                _lastGameServerStatus = gameServerStatus;
            }
        }

        protected override async Task ProcessMessageAsync(string clientId, Message message)
        {
            Logger.System(LogLevel.Info, $"Processing message from {clientId}: Type={message.Type}");
            
            if (message.Type != MessageType.GameServerHeartbeat)
            {
                Logger.System(LogLevel.Debug, $"Received message from {clientId}: {message.Type}");
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
                    Logger.System(LogLevel.Warning, $"Unhandled message type: {message.Type}");
                    break;
            }
        }

        private async Task HandleGameServerRegistrationAsync(string clientId, Message message)
        {
            Logger.Connection(LogLevel.Info, $"Processing game server registration from {clientId.Substring(0, 6)}...");
            var registrationData = message.GetData<GameServerRegistrationData>();
            
            if (registrationData == null)
            {
                Logger.Error($"Invalid game server registration data from {clientId.Substring(0, 6)}");
                var failResponse = new GameServerRegistrationResponse
                {
                    Success = false,
                    Error = "Invalid registration data"
                };
                await SendMessageAsync(clientId, Message.Create<GameServerRegistrationResponse>(MessageType.RegisterGameServer, failResponse));
                return;
            }
            
            Logger.Connection(LogLevel.Debug, $"Registration data received: ServerId={registrationData.ServerId.Substring(0, 6)}, Endpoint={registrationData.Endpoint}, MaxPlayers={registrationData.MaxPlayers}");
            
            var gameServer = new GameServerInfo
            {
                Id = registrationData.ServerId,
                Endpoint = registrationData.Endpoint,
                MaxPlayers = registrationData.MaxPlayers,
                LastHeartbeat = DateTime.UtcNow
            };
            
            _gameServers[gameServer.Id] = gameServer;
            
            Logger.Connection(LogLevel.Info, $"Game server registered: {gameServer.Id.Substring(0, 6)} at {gameServer.Endpoint}");
            
            // Acknowledge registration
            var response = new GameServerRegistrationResponse
            {
                Success = true
            };
            await SendMessageAsync(clientId, Message.Create<GameServerRegistrationResponse>(MessageType.RegisterGameServer, response));
            Logger.Connection(LogLevel.Debug, $"Registration acknowledgment sent to {clientId.Substring(0, 6)}");
        }

        private Task HandleGameServerHeartbeatAsync(string clientId, Message message)
        {
            var heartbeatData = message.GetData<GameServerHeartbeatData>();
            
            if (heartbeatData == null || !_gameServers.TryGetValue(heartbeatData.ServerId, out var gameServer))
            {
                Logger.Error($"Invalid game server heartbeat data or unknown server from {clientId.Substring(0, 6)}");
                return Task.CompletedTask;
            }
            
            // Only log changes in player count
            if (gameServer.CurrentPlayers != heartbeatData.CurrentPlayers)
            {
                Logger.GameState(LogLevel.Info, $"Server {heartbeatData.ServerId.Substring(0, 6)} player count changed: {gameServer.CurrentPlayers} -> {heartbeatData.CurrentPlayers}");
            }
            
            gameServer.CurrentPlayers = heartbeatData.CurrentPlayers;
            gameServer.LastHeartbeat = DateTime.UtcNow;
            
            return Task.CompletedTask;
        }

        private async Task HandleClientConnectAsync(string clientId, Message message)
        {
            Logger.Connection(LogLevel.Info, $"Processing client connection request from {clientId}");
            
            try
            {
                // Extract client data if provided
                var requestData = message.GetData<JsonElement>();
                string clientIdentifier = null;
                
                if (requestData.ValueKind == JsonValueKind.Object && requestData.TryGetProperty("ClientId", out var clientIdProp))
                {
                    clientIdentifier = clientIdProp.GetString();
                    Logger.Connection(LogLevel.Debug, $"Client identified itself as: {clientIdentifier}");
                }
                
                // Log available servers
                Logger.Connection(LogLevel.Debug, $"Available game servers: {_gameServers.Count}");
                foreach (var server in _gameServers.Values)
                {
                    Logger.Connection(LogLevel.Debug, $" - Server {server.Id.Substring(0, 6)}: Endpoint={server.Endpoint}, Players={server.CurrentPlayers}/{server.MaxPlayers}, Available={server.IsAvailable}");
                }
                
                // Find the game server with the least number of players
                var bestServer = _gameServers.Values
                    .Where(s => s.IsAvailable)
                    .OrderBy(s => s.CurrentPlayers)
                    .FirstOrDefault();
                    
                if (bestServer == null)
                {
                    Logger.Connection(LogLevel.Warning, $"No available game servers for client {clientId}");
                    var failResponse = new ClientConnectResponse
                    {
                        Success = false,
                        Error = "No available game servers"
                    };
                    await SendMessageAsync(clientId, Message.Create<ClientConnectResponse>(MessageType.ClientConnect, failResponse));
                    return;
                }
                
                _clientToGameServerMap[clientId] = bestServer.Id;
                
                // Send the game server info to the client
                var response = new ClientConnectResponse
                {
                    Success = true,
                    ServerEndpoint = bestServer.Endpoint
                };
                
                Logger.Connection(LogLevel.Info, $"Sending success response to client {clientId}: {response}");
                await SendMessageAsync(clientId, Message.Create<ClientConnectResponse>(MessageType.ClientConnect, response));
                
                Logger.Connection(LogLevel.Info, $"Client {clientId} routed to game server {bestServer.Id.Substring(0, 6)} at {bestServer.Endpoint}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error handling client connection request from {clientId}: {ex.Message}");
                var failResponse = new ClientConnectResponse
                {
                    Success = false,
                    Error = $"Internal server error: {ex.Message}"
                };
                await SendMessageAsync(clientId, Message.Create<ClientConnectResponse>(MessageType.ClientConnect, failResponse));
            }
        }

        private Task HandleClientDisconnectAsync(string clientId)
        {
            _clientToGameServerMap.TryRemove(clientId, out _);
            Logger.Connection(LogLevel.Info, $"Client {clientId.Substring(0, 6)} disconnected from master server");
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