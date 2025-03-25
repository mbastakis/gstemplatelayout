using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Common.Logging;
using Common.Models;
using Common.Networking;
using System.Text.Json;
using System.Linq;
using System.Numerics;
using System.Net;
using System.Text;

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
        private HealthServer _healthServer;
        private bool _masterServerConnected = false;
        private bool _registrationSuccess = false;

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
                
                // Start health check server
                StartHealthServer();
                
                Logger.Connection(LogLevel.Info, "Attempting to connect to master server...");
                await ConnectToMasterServerAsync();
                Logger.Connection(LogLevel.Info, "Successfully connected to master server");
                _masterServerConnected = true;
                
                _heartbeatTimer = new Timer(SendHeartbeat, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
                Logger.System(LogLevel.Info, "Heartbeat timer started");
                
                Logger.System(LogLevel.Info, "Game Server is ready");
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
            
            // Stop health server
            _healthServer?.Stop();
            _healthServer?.Dispose();
            
            base.Stop();
            Logger.System(LogLevel.Info, "Game server stopped");
        }
        
        private void StartHealthServer()
        {
            try
            {
                _healthServer = new HealthServer("GameServer", 8081);
                _healthServer.IsRunning = true;
                
                // Set additional status information
                _healthServer.GetAdditionalStatus = () => 
                    $"Server ID: {_serverId.Substring(0, 6)}\n" +
                    $"Connected to Master: {_masterServerConnected}\n" +
                    $"Registration Success: {_registrationSuccess}\n" +
                    $"Players: {_players.Count}/{_maxPlayers}\n" +
                    $"Connected Clients: {_clients.Count}";
                
                _healthServer.Start();
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to start health check server: {ex.Message}", ex);
            }
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
                    
                    // Register with master server - use the service name which should be resolvable by all pods
                    string gameServerEndpoint = "game-server:7100";
                    Logger.Connection(LogLevel.Info, $"Using service name for registration: {gameServerEndpoint}");
                    
                    var registrationData = new GameServerRegistrationData
                    {
                        ServerId = _serverId,
                        Endpoint = gameServerEndpoint,
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
                    _registrationSuccess = success;
                    
                    // Update health check readiness based on registration success
                    _healthServer.IsReady = success && _masterServerConnected;
                    
                    Logger.Connection(LogLevel.Info, $"Registration response: {(success ? "Success" : "Failed")}");
                    break;
                    
                default:
                    Logger.Connection(LogLevel.Warning, $"Unhandled message type from master server: {message.Type}");
                    break;
            }
        }

        private async void OnMasterServerDisconnected(object sender, EventArgs e)
        {
            _masterServerConnected = false;
            _registrationSuccess = false;
            
            // Update health check readiness
            _healthServer.IsReady = false;
            
            Logger.Connection(LogLevel.Warning, "Disconnected from master server. Attempting to reconnect...");
            
            // Wait a bit before reconnecting
            await Task.Delay(5000);
            
            try
            {
                Logger.Connection(LogLevel.Info, $"Reconnecting to master server at {_masterServerHost}:{_masterServerPort}...");
                await ConnectToMasterServerAsync();
                _masterServerConnected = true;
                Logger.Connection(LogLevel.Info, "Successfully reconnected to master server and re-registered as {_serverId.Substring(0, 6)}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to reconnect to master server: {ex.Message}", ex);
                
                // Schedule another reconnection attempt
                _ = Task.Run(async () => 
                {
                    Logger.Connection(LogLevel.Info, "Scheduling another reconnection attempt in 10 seconds...");
                    await Task.Delay(10000);
                    await ConnectToMasterServerAsync();
                    _masterServerConnected = true;
                });
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
                    var failResponse = new PlayerJoinResponse
                    {
                        Success = false,
                        Error = "Server is full"
                    };
                    await SendMessageAsync(clientId, Message.Create<PlayerJoinResponse>(MessageType.PlayerJoin, failResponse));
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
                
                // Notify the player that they've joined successfully
                var response = new PlayerJoinResponse
                {
                    Success = true
                };
                await SendMessageAsync(clientId, Message.Create<PlayerJoinResponse>(MessageType.PlayerJoin, response));
                
                // Notify all other players that a new player has joined
                var notification = new PlayerJoinNotification
                {
                    PlayerId = clientId
                };
                await BroadcastMessageAsync(Message.Create<PlayerJoinNotification>(MessageType.PlayerJoin, notification), clientId);
                
                Logger.GameState(LogLevel.Info, $"Player {clientId.Substring(0, 6)} joined successfully");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error processing player join: {ex.Message}", ex);
                var failResponse = new PlayerJoinResponse
                {
                    Success = false,
                    Error = "Internal server error"
                };
                await SendMessageAsync(clientId, Message.Create<PlayerJoinResponse>(MessageType.PlayerJoin, failResponse));
            }
        }

        private async Task HandlePlayerDisconnectAsync(string clientId)
        {
            if (_players.TryRemove(clientId, out _))
            {
                Logger.GameState(LogLevel.Info, $"Player {clientId.Substring(0, 6)} disconnected. Total players: {_players.Count}");
                
                // Notify all other players
                var notification = new PlayerLeaveNotification
                {
                    PlayerId = clientId
                };
                await BroadcastMessageAsync(Message.Create<PlayerLeaveNotification>(MessageType.PlayerLeave, notification));
            }
        }

        private async Task HandlePlayerActionAsync(string clientId, PlayerAction action)
        {
            try
            {
                if (!_players.TryGetValue(clientId, out var playerInfo))
                {
                    Logger.Error($"Received action from unknown player {clientId.Substring(0, 6)}");
                    return;
                }

                // Simulate intensive game server operations
                await SimulateGameServerLoadAsync();

                // Update player state based on action
                switch (action.Type)
                {
                    case ActionType.Move:
                        playerInfo.Position += action.Value * 0.1f;
                        break;
                    case ActionType.Rotate:
                        playerInfo.Rotation += action.Value * 0.1f;
                        break;
                    case ActionType.Scale:
                        playerInfo.Scale = Math.Max(0.1f, Math.Min(2.0f, playerInfo.Scale + action.Value * 0.01f));
                        break;
                }

                // Broadcast updated state to all players
                var update = new PlayerUpdateMessage
                {
                    PlayerId = clientId,
                    Position = playerInfo.Position,
                    Rotation = playerInfo.Rotation,
                    Scale = playerInfo.Scale
                };

                await BroadcastMessageAsync(Message.Create<PlayerUpdateMessage>(MessageType.PlayerUpdate, update));
                
                // Also send a GameStateUpdate for more detailed state information
                var stateUpdate = new GameStateUpdate
                {
                    PlayerId = clientId,
                    Position = playerInfo.Position,
                    Rotation = playerInfo.Rotation,
                    Scale = playerInfo.Scale
                };

                await BroadcastMessageAsync(Message.Create<GameStateUpdate>(MessageType.GameStateUpdate, stateUpdate));
            }
            catch (Exception ex)
            {
                Logger.Error($"Error handling player action: {ex.Message}", ex);
            }
        }

        private async Task SimulateGameServerLoadAsync()
        {
            // Simulate physics calculations
            for (int i = 0; i < 1000; i++)
            {
                // Simulate collision detection
                foreach (var player1 in _players.Values)
                {
                    foreach (var player2 in _players.Values)
                    {
                        if (player1 == player2) continue;
                        
                        // Calculate distance between players
                        float dx = player1.Position - player2.Position;
                        float distance = Math.Abs(dx);
                        
                        // Simulate collision response
                        if (distance < 1.0f)
                        {
                            float overlap = 1.0f - distance;
                            player1.Position += overlap * 0.5f;
                            player2.Position -= overlap * 0.5f;
                        }
                    }
                }
                
                // Simulate state updates
                foreach (var player in _players.Values)
                {
                    // Update player physics
                    player.Position += 0.001f; // Simulate constant movement
                    player.Rotation += 0.001f; // Simulate constant rotation
                    
                    // Clamp values to reasonable ranges
                    player.Position = Math.Max(-100f, Math.Min(100f, player.Position));
                    player.Rotation = player.Rotation % 360f;
                }
            }
        }

        protected override async Task OnClientDisconnectedAsync(string clientId)
        {
            Logger.Connection(LogLevel.Info, $"Client {clientId.Substring(0, 6)} disconnected from game server");
            await HandlePlayerDisconnectAsync(clientId);
        }

        // Helper method to get pod IP if running in Kubernetes
        private string GetPodIp()
        {
            try
            {
                // In Kubernetes, this env var contains the pod IP
                var podIp = Environment.GetEnvironmentVariable("POD_IP");
                if (!string.IsNullOrEmpty(podIp))
                {
                    return podIp;
                }
                
                // Try to get the IP address directly
                var hostEntry = System.Net.Dns.GetHostEntry(Environment.MachineName);
                if (hostEntry.AddressList.Length > 0)
                {
                    // Prefer IPv4 address
                    var ipv4Address = hostEntry.AddressList
                        .FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                    
                    if (ipv4Address != null)
                    {
                        return ipv4Address.ToString();
                    }
                    
                    // Fall back to any address
                    return hostEntry.AddressList[0].ToString();
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error determining pod IP: {ex.Message}");
            }
            
            return null;
        }

        private BigInteger CalculateFibonacci(int n)
       {
            if (n <= 1)
                return n;

            BigInteger a = 0;
            BigInteger b = 1;
            for (int i = 2; i <= n; i++)
            {
                var temp = a + b;
                a = b;
                b = temp;
            }
            return b;
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