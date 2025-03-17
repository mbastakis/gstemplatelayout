using System;
using System.Text.Json;
using System.Threading.Tasks;
using Common.Logging;
using Common.Models;

namespace Common.Networking
{
    public class GameClient : SocketClient
    {
        private readonly string _masterServerHost;
        private readonly int _masterServerPort;
        private string _gameServerHost;
        private int _gameServerPort;
        private bool _connectedToGameServer = false;
        private SocketClient _gameServerConnection;

        public string ClientId { get; private set; }
        public event EventHandler<string> ActionSent;
        public new event EventHandler Connected;
        public new event EventHandler Disconnected;

        // Override IsConnected to check if connected to game server
        public override bool IsConnected
        {
            get
            {
                var isConnected = _connectedToGameServer && _gameServerConnection?.IsConnected == true;
                Logger.Connection(LogLevel.Debug, $"IsConnected check for Client {ClientId.Substring(0, 6)}: {isConnected} (GameServer: {_connectedToGameServer}, Connection: {_gameServerConnection?.IsConnected})");
                return isConnected;
            }
        }

        public GameClient(string masterServerHost, int masterServerPort) : base(masterServerHost, masterServerPort)
        {
            _masterServerHost = masterServerHost;
            _masterServerPort = masterServerPort;
            ClientId = Guid.NewGuid().ToString();
            base.Connected += (s, e) => OnMasterServerConnected();
            // Don't forward base.Disconnected events - we only want to fire Disconnected for game server disconnections
        }

        private void OnMasterServerConnected()
        {
            Logger.Connection(LogLevel.Info, $"Client {ClientId.Substring(0, 6)} connected to master server");
            
            // This will be sent automatically by OnConnectedAsync
        }

        protected override async Task OnConnectedAsync()
        {
            // Send a client connect request to the master server
            var connectMessage = new Message
            {
                Type = MessageType.ClientConnect,
                Data = JsonSerializer.Serialize(new { ClientId })
            };
            
            Logger.Connection(LogLevel.Debug, $"Sending ClientConnect to master server: {ClientId.Substring(0, 6)}");
            await SendMessageAsync(connectMessage);
        }

        protected override async Task OnMessageReceivedAsync(Message message)
        {
            // Handle messages from either master server or game server
            if (!_connectedToGameServer)
            {
                // Messages from master server
                await HandleMasterServerMessageAsync(message);
            }
            else
            {
                // Messages from game server
                await HandleGameServerMessageAsync(message);
            }
        }

        private async Task HandleMasterServerMessageAsync(Message message)
        {
            switch (message.Type)
            {
                case MessageType.ClientConnect:
                    // Response from master server about which game server to connect to
                    var response = message.GetData<JsonElement>();
                    if (response.TryGetProperty("Success", out JsonElement successProp) && successProp.GetBoolean())
                    {
                        if (response.TryGetProperty("ServerEndpoint", out JsonElement endpointProp))
                        {
                            var endpoint = endpointProp.GetString();
                            Logger.Connection(LogLevel.Info, $"Assigned to game server at {endpoint}");
                            
                            // Parse the endpoint (format: hostname:port)
                            var parts = endpoint.Split(':');
                            if (parts.Length == 2 && int.TryParse(parts[1], out int port))
                            {
                                _gameServerHost = parts[0];
                                _gameServerPort = port;
                                
                                // Disconnect from master server and connect to game server
                                await ConnectToGameServerAsync();
                            }
                            else
                            {
                                Logger.Error($"Invalid server endpoint format: {endpoint}");
                            }
                        }
                    }
                    else if (response.TryGetProperty("Error", out JsonElement errorProp))
                    {
                        Logger.Error($"Failed to get game server assignment: {errorProp.GetString()}");
                    }
                    break;
                    
                default:
                    Logger.System(LogLevel.Warning, $"Received unknown message type from master server: {message.Type}");
                    break;
            }
        }

        private async Task HandleGameServerMessageAsync(Message message)
        {
            switch (message.Type)
            {
                case MessageType.PlayerJoin:
                    var response = message.GetData<JsonElement>();
                    if (response.TryGetProperty("Success", out JsonElement successProp) && successProp.GetBoolean())
                    {
                        Logger.Connection(LogLevel.Info, $"Client {ClientId.Substring(0, 6)} successfully joined game server");
                        // Notify that we're fully connected to the game server
                        Connected?.Invoke(this, EventArgs.Empty);
                    }
                    else if (response.TryGetProperty("Error", out JsonElement errorProp))
                    {
                        Logger.Error($"Failed to join game server: {errorProp.GetString()}");
                    }
                    break;
                    
                case MessageType.PlayerLeave:
                    // A player left the game - handle notification
                    var leaveData = message.GetData<JsonElement>();
                    if (leaveData.TryGetProperty("PlayerId", out JsonElement playerIdProp))
                    {
                        string playerId = playerIdProp.GetString();
                        Logger.GameState(LogLevel.Info, $"Player {playerId?.Substring(0, 6)} left the game");
                    }
                    break;
                    
                case MessageType.PlayerUpdate:
                    // Handle player update messages
                    Logger.GameState(LogLevel.Debug, $"Received player update");
                    break;
                    
                default:
                    Logger.System(LogLevel.Warning, $"Received unknown message type from game server: {message.Type}");
                    break;
            }
        }

        private async Task ConnectToGameServerAsync()
        {
            try
            {
                // Disconnect from master server - but don't trigger our Disconnected event
                base.Disconnect();
                
                // Connect to game server
                Logger.Connection(LogLevel.Info, $"Connecting to game server at {_gameServerHost}:{_gameServerPort}");
                _gameServerConnection = new SocketClient(_gameServerHost, _gameServerPort);
                _gameServerConnection.MessageReceived += async (sender, msg) => await OnMessageReceivedAsync(msg);
                _gameServerConnection.Disconnected += (sender, args) => 
                {
                    _connectedToGameServer = false;
                    // Only fire Disconnected when the game server connection is lost
                    Disconnected?.Invoke(this, EventArgs.Empty);
                };
                
                await _gameServerConnection.ConnectAsync();
                _connectedToGameServer = true;
                
                // Send join request to game server
                var joinMessage = new Message
                {
                    Type = MessageType.PlayerJoin,
                    Data = JsonSerializer.Serialize(new { ClientId })
                };
                
                await _gameServerConnection.SendMessageAsync(joinMessage);
                Logger.Connection(LogLevel.Debug, $"Sent PlayerJoin to game server: {ClientId.Substring(0, 6)}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error connecting to game server: {ex.Message}");
                throw;
            }
        }

        public async Task SendRandomActionAsync()
        {
            if (!_connectedToGameServer || _gameServerConnection == null)
            {
                Logger.Error($"Cannot send action for client {ClientId.Substring(0, 6)} - not connected to game server");
                return;
            }
            
            var action = new PlayerAction
            {
                Type = (ActionType)new Random().Next(0, 3),
                Value = new Random().Next(0, 100)
            };

            var message = new Message
            {
                Type = MessageType.PlayerAction,
                Data = JsonSerializer.Serialize(action)
            };
            
            Logger.PlayerAction(LogLevel.Info, $"CLIENT ACTION: {ClientId.Substring(0, 6)} sending {action.Type}={action.Value}");
            await _gameServerConnection.SendMessageAsync(message);
            ActionSent?.Invoke(this, action.ToString());
        }

        public new void Disconnect()
        {
            try
            {
                _gameServerConnection?.Disconnect();
                base.Disconnect();
            }
            catch (Exception ex)
            {
                Logger.Error($"Error disconnecting client {ClientId.Substring(0, 6)}", ex);
            }
        }
    }
} 