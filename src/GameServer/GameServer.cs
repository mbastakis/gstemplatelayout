using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Common.Logging;
using Common.Models;
using Common.Networking;
using System.Text.Json;

namespace GameServer
{
    public class GameServer : SocketServer
    {
        private readonly string _serverId;
        private readonly string _masterServerHost;
        private readonly int _masterServerPort;
        private readonly int _maxPlayers;
        private SocketClient _masterServerClient;
        private Timer _heartbeatTimer;
        private readonly ConcurrentDictionary<string, PlayerInfo> _players = new ConcurrentDictionary<string, PlayerInfo>();

        public GameServer(int port, string masterServerHost, int masterServerPort, int maxPlayers = 100) 
            : base("GameServer", port)
        {
            _serverId = Guid.NewGuid().ToString();
            _masterServerHost = masterServerHost;
            _masterServerPort = masterServerPort;
            _maxPlayers = maxPlayers;
            
            Logger.Initialize("GameServer", "logs/game-server.log");
        }

        public override async Task Start()
        {
            try
            {
                Logger.System(LogLevel.Info, "Starting Game Server...");
                await base.Start();
                Logger.System(LogLevel.Info, "Base server started successfully");
                
                Logger.Connection(LogLevel.Info, "Attempting to connect to master server...");
                await ConnectToMasterServerAsync();
                Logger.Connection(LogLevel.Info, "Successfully connected to master server");
                
                _heartbeatTimer = new Timer(SendHeartbeat, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
                Logger.System(LogLevel.Info, "Heartbeat timer started");
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to start game server", ex);
                Stop(); // Make sure we clean up
                throw;
            }
        }

        public override void Stop()
        {
            _heartbeatTimer?.Dispose();
            _masterServerClient?.Dispose();
            base.Stop();
            Logger.System(LogLevel.Info, "Game server stopped");
        }

        private async Task ConnectToMasterServerAsync()
        {
            int retryCount = 0;
            const int maxRetries = 3;
            
            while (retryCount < maxRetries)
            {
                try
                {
                    Logger.Connection(LogLevel.Info, $"Attempting to connect to master server at {_masterServerHost}:{_masterServerPort}...");
                    _masterServerClient = new SocketClient(_masterServerHost, _masterServerPort);
                    
                    _masterServerClient.MessageReceived += OnMasterServerMessageReceived;
                    _masterServerClient.Disconnected += OnMasterServerDisconnected;
                    
                    await _masterServerClient.ConnectAsync();
                    Logger.Connection(LogLevel.Info, "Successfully connected to master server");
                    
                    // Register with master server
                    var registrationData = new GameServerRegistrationData
                    {
                        ServerId = _serverId,
                        Endpoint = $"{Environment.MachineName}:{_port}",
                        MaxPlayers = _maxPlayers
                    };
                    
                    Logger.Connection(LogLevel.Info, $"Registering game server with ID {_serverId.Substring(0, 6)}...");
                    await _masterServerClient.SendMessageAsync(Message.Create<GameServerRegistrationData>(MessageType.RegisterGameServer, registrationData));
                    
                    Logger.Connection(LogLevel.Info, $"Registered with master server as {_serverId.Substring(0, 6)}");
                    return; // Success, exit the retry loop
                }
                catch (Exception ex)
                {
                    retryCount++;
                    Logger.Error($"Failed to connect to master server (attempt {retryCount}/{maxRetries}): {ex.Message}");
                    
                    if (retryCount < maxRetries)
                    {
                        Logger.Connection(LogLevel.Warning, "Retrying in 5 seconds...");
                        await Task.Delay(5000);
                    }
                    else
                    {
                        Logger.Connection(LogLevel.Error, "Max retries reached. Giving up.");
                        throw;
                    }
                }
            }
        }

        private void OnMasterServerMessageReceived(object sender, Message message)
        {
            Logger.Connection(LogLevel.Debug, $"Received message from master server: {message.Type}");
            
            switch (message.Type)
            {
                case MessageType.RegisterGameServer:
                    // Registration acknowledgment
                    var response = message.GetData<dynamic>();
                    var success = response.GetProperty("Success").GetBoolean();
                    Logger.Connection(LogLevel.Info, $"Registration response: {(success ? "Success" : "Failed")}");
                    break;
                    
                default:
                    Logger.Connection(LogLevel.Warning, $"Unhandled message type from master server: {message.Type}");
                    break;
            }
        }

        private async void OnMasterServerDisconnected(object sender, EventArgs e)
        {
            Logger.Connection(LogLevel.Warning, "Disconnected from master server. Attempting to reconnect...");
            
            // Wait a bit before reconnecting
            await Task.Delay(5000);
            
            try
            {
                await ConnectToMasterServerAsync();
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to reconnect to master server", ex);
            }
        }

        private async void SendHeartbeat(object state)
        {
            if (_masterServerClient == null)
                return;
                
            try
            {
                var heartbeatData = new GameServerHeartbeatData
                {
                    ServerId = _serverId,
                    CurrentPlayers = _players.Count,
                    Timestamp = DateTime.UtcNow
                };
                
                await _masterServerClient.SendMessageAsync(Message.Create<GameServerHeartbeatData>(MessageType.GameServerHeartbeat, heartbeatData));
            }
            catch (Exception ex)
            {
                Logger.Error("Error sending heartbeat", ex);
            }
        }

        protected override async Task ProcessMessageAsync(string clientId, Message message)
        {
            if (message.Type == MessageType.PlayerAction)
            {
                Logger.System(LogLevel.Info, $"RECEIVED MESSAGE: Player {clientId.Substring(0, 6)} sending action");
            }
            else
            {
                Logger.System(LogLevel.Debug, $"Received message from client {clientId.Substring(0, 6)}: {message.Type}");
            }
            
            switch (message.Type)
            {
                case MessageType.PlayerJoin:
                    await HandlePlayerJoinAsync(clientId);
                    break;
                    
                case MessageType.PlayerAction:
                    try
                    {
                        var actionData = message.GetData<PlayerAction>();
                        if (actionData != null)
                        {
                            Logger.GameState(LogLevel.Debug, $"Processing action from player {clientId.Substring(0, 6)}: {actionData.Type} = {actionData.Value}");
                            await HandlePlayerActionAsync(clientId, actionData);
                        }
                        else
                        {
                            Logger.Error($"Invalid action data from client {clientId.Substring(0, 6)}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Error processing action from client {clientId.Substring(0, 6)}: {ex.Message}");
                    }
                    break;
                    
                default:
                    Logger.System(LogLevel.Warning, $"Unknown message type from client {clientId.Substring(0, 6)}: {message.Type}");
                    break;
            }
        }

        private async Task HandlePlayerJoinAsync(string clientId)
        {
            try
            {
                Logger.GameState(LogLevel.Info, $"Processing join request from player {clientId.Substring(0, 6)}");
                
                if (_players.Count >= _maxPlayers)
                {
                    Logger.GameState(LogLevel.Warning, $"Server is full, rejecting player {clientId.Substring(0, 6)}");
                    await SendMessageAsync(clientId, Message.Create<object>(MessageType.PlayerJoin, new { Success = false, Error = "Server is full" }));
                    return;
                }
                
                // Log more details about the connection process
                Logger.Connection(LogLevel.Debug, $"Creating player info for client ID {clientId.Substring(0, 6)}");
                
                var playerInfo = new PlayerInfo
                {
                    Id = clientId,
                    JoinTime = DateTime.UtcNow
                };
                
                _players[clientId] = playerInfo;
                
                Logger.GameState(LogLevel.Info, $"Player {clientId.Substring(0, 6)} joined. Total players: {_players.Count}");
                
                // Notify the player they've joined successfully
                var response = new { Success = true };
                Logger.Connection(LogLevel.Debug, $"Sending success response to player {clientId.Substring(0, 6)}: {JsonSerializer.Serialize(response)}");
                
                await SendMessageAsync(clientId, Message.Create<object>(MessageType.PlayerJoin, response));
                
                // Notify all other players about the new player
                await BroadcastMessageAsync(Message.Create<object>(MessageType.PlayerJoin, new { PlayerId = clientId }), clientId);
            }
            catch (Exception ex)
            {
                Logger.Error($"Error handling player join request", ex);
                await SendMessageAsync(clientId, Message.Create<object>(MessageType.PlayerJoin, new { Success = false, Error = "Internal server error" }));
            }
        }

        private async Task HandlePlayerLeaveAsync(string clientId)
        {
            if (_players.TryRemove(clientId, out _))
            {
                Logger.GameState(LogLevel.Info, $"Player {clientId.Substring(0, 6)} left. Total players: {_players.Count}");
                
                // Notify all other players
                await BroadcastMessageAsync(Message.Create<object>(MessageType.PlayerLeave, new { PlayerId = clientId }));
            }
        }

        private async Task HandlePlayerActionAsync(string clientId, PlayerAction action)
        {
            if (!_players.TryGetValue(clientId, out var player))
            {
                Logger.System(LogLevel.Warning, $"Received action from unknown player {clientId.Substring(0, 6)}");
                return;
            }

            // Process the action
            Logger.GameState(LogLevel.Info, $"SERVER RECEIVED: Action from {clientId.Substring(0, 6)}: {action.Type}={action.Value}");
            
            switch (action.Type)
            {
                case ActionType.Move:
                    player.Position += action.Value;
                    break;
                case ActionType.Rotate:
                    player.Rotation += action.Value;
                    break;
                case ActionType.Scale:
                    player.Scale *= (1 + action.Value / 100.0f);
                    break;
            }

            // Broadcast the updated player state to all other players
            var updateMessage = new Message
            {
                Type = MessageType.PlayerUpdate,
                Data = JsonSerializer.Serialize(player)
            };

            Logger.GameState(LogLevel.Info, $"SERVER BROADCAST: Player {clientId.Substring(0, 6)} update: Pos={player.Position}, Rot={player.Rotation}, Scale={player.Scale:.2f}");
            await BroadcastMessageAsync(updateMessage, clientId);
        }

        protected override async Task OnClientDisconnectedAsync(string clientId)
        {
            Logger.Connection(LogLevel.Info, $"Client {clientId.Substring(0, 6)} disconnected from game server");
            await HandlePlayerLeaveAsync(clientId);
        }
    }

    public class PlayerInfo
    {
        public string Id { get; set; }
        public DateTime JoinTime { get; set; }
        public float Position { get; set; }
        public float Rotation { get; set; }
        public float Scale { get; set; } = 1.0f;
    }
} 