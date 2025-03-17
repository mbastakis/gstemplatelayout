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
            if (message.Type != MessageType.GameServerHeartbeat)
            {
                Logger.System(LogLevel.Debug, $"Received message from {clientId.Substring(0, 6)}: {message.Type}");
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
                await SendMessageAsync(clientId, Message.Create<object>(MessageType.RegisterGameServer, new { Success = false, Error = "Invalid registration data" }));
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
            await SendMessageAsync(clientId, Message.Create<object>(MessageType.RegisterGameServer, new { Success = true }));
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
            Logger.Connection(LogLevel.Info, $"Processing client connection request from {clientId.Substring(0, 6)}");
            
            try
            {
                // Find the game server with the least number of players
                var bestServer = _gameServers.Values
                    .Where(s => s.IsAvailable)
                    .OrderBy(s => s.CurrentPlayers)
                    .FirstOrDefault();
                    
                if (bestServer == null)
                {
                    Logger.Connection(LogLevel.Warning, "No available game servers for client connection");
                    await SendMessageAsync(clientId, Message.Create<object>(MessageType.ClientConnect, new { Success = false, Error = "No available game servers" }));
                    return;
                }
                
                _clientToGameServerMap[clientId] = bestServer.Id;
                
                // Send the game server info to the client
                var response = new { Success = true, ServerEndpoint = bestServer.Endpoint };
                Logger.Connection(LogLevel.Debug, $"Sending success response to client {clientId.Substring(0, 6)}: {JsonSerializer.Serialize(response)}");
                
                await SendMessageAsync(clientId, Message.Create<object>(MessageType.ClientConnect, response));
                
                Logger.Connection(LogLevel.Info, $"Client {clientId.Substring(0, 6)} routed to game server {bestServer.Id.Substring(0, 6)} at {bestServer.Endpoint}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error handling client connection request", ex);
                await SendMessageAsync(clientId, Message.Create<object>(MessageType.ClientConnect, new { Success = false, Error = "Internal server error" }));
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
                    Logger.Connection(LogLevel.Info, $"Client {clientId} connected from {remoteEndPoint?.Address}:{remoteEndPoint?.Port}");
                    
                    var buffer = new byte[4096];
                    var stream = client.GetStream();
                    
                    while (!cancellationToken.IsCancellationRequested && client.Connected)
                    {
                        var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                        
                        if (bytesRead == 0)
                            break;
                            
                        var messageJson = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                        Logger.Connection(LogLevel.Debug, $"Received message from {clientId}: {messageJson}");
                        
                        try
                        {
                            var message = Message.Deserialize(messageJson);
                            await ProcessMessageAsync(clientId, message);
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"Error processing message: {ex.Message}");
                            Logger.Error($"Stack trace: {ex.StackTrace}");
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
                Logger.Error($"Stack trace: {ex.StackTrace}");
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