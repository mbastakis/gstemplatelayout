using System;
using System.Text.Json;
using System.Threading.Tasks;
using Common.Logging;
using Common.Models;
using Common.Networking;

namespace ClientSimulator
{
    public class GameClient
    {
        private readonly string _clientId;
        private readonly string _masterServerHost;
        private readonly int _masterServerPort;
        private SocketClient _masterServerClient;
        private SocketClient _gameServerClient;
        private string _gameServerEndpoint;
        private bool _isConnectedToGameServer;
        private readonly Random _random = new Random();
        
        public bool IsConnected => _isConnectedToGameServer;
        
        public event EventHandler Connected;
        public event EventHandler Disconnected;
        public event EventHandler<string> ActionSent;

        public GameClient(string masterServerHost, int masterServerPort)
        {
            _clientId = Guid.NewGuid().ToString().Substring(0, 6);
            _masterServerHost = masterServerHost;
            _masterServerPort = masterServerPort;
        }

        public async Task ConnectAsync()
        {
            try
            {
                Logger.Connection(LogLevel.Debug, $"[Client {_clientId}] Connecting to master server at {_masterServerHost}:{_masterServerPort}...");
                
                _masterServerClient = new SocketClient(_masterServerHost, _masterServerPort);
                _masterServerClient.MessageReceived += OnMessageReceived;
                _masterServerClient.Disconnected += OnMasterServerDisconnected;
                
                await _masterServerClient.ConnectAsync();
                
                Logger.Connection(LogLevel.Debug, $"[Client {_clientId}] Connected to master server");
                
                // Request game server assignment
                await _masterServerClient.SendMessageAsync(Message.Create<object>(MessageType.ClientConnect, null));
                Logger.Connection(LogLevel.Debug, $"[Client {_clientId}] Sent connection request to master server");
            }
            catch (Exception ex)
            {
                Logger.Error($"[Client {_clientId}] Failed to connect to master server: {ex.Message}");
                throw;
            }
        }

        public async Task DisconnectAsync()
        {
            Logger.Connection(LogLevel.Debug, $"[Client {_clientId}] Disconnecting from servers");
            
            if (_gameServerClient != null)
            {
                try
                {
                    await _gameServerClient.SendMessageAsync(Message.Create<object>(MessageType.PlayerLeave, null));
                    _gameServerClient.Dispose();
                    Logger.Connection(LogLevel.Debug, $"[Client {_clientId}] Disconnected from game server");
                }
                catch (Exception ex)
                {
                    Logger.Error($"[Client {_clientId}] Error disconnecting from game server: {ex.Message}");
                }
            }
            
            if (_masterServerClient != null)
            {
                try
                {
                    await _masterServerClient.SendMessageAsync(Message.Create<object>(MessageType.ClientDisconnect, null));
                    _masterServerClient.Dispose();
                    Logger.Connection(LogLevel.Debug, $"[Client {_clientId}] Disconnected from master server");
                }
                catch (Exception ex)
                {
                    Logger.Error($"[Client {_clientId}] Error disconnecting from master server: {ex.Message}");
                }
            }
            
            _isConnectedToGameServer = false;
            Logger.Connection(LogLevel.Debug, $"[Client {_clientId}] Disconnected from servers");
            Disconnected?.Invoke(this, EventArgs.Empty);
        }

        private void OnMessageReceived(object sender, Message message)
        {
            Logger.Connection(LogLevel.Debug, $"[Client {_clientId}] Received message from master server: {message.Type}");
            
            switch (message.Type)
            {
                case MessageType.ClientConnect:
                    var response = message.GetData<JsonElement>();
                    if (response.ValueKind == JsonValueKind.Object)
                    {
                        if (response.TryGetProperty("Success", out var success) && success.GetBoolean())
                        {
                            if (response.TryGetProperty("ServerEndpoint", out var serverEndpoint))
                            {
                                var endpoint = serverEndpoint.GetString();
                                var parts = endpoint.Split(':');
                                if (parts.Length == 2 && int.TryParse(parts[1], out int port))
                                {
                                    _gameServerEndpoint = endpoint;
                                    _ = ConnectToGameServerAsync();
                                }
                            }
                        }
                        else if (response.TryGetProperty("Error", out var error))
                        {
                            Logger.Connection(LogLevel.Warning, $"[Client {_clientId}] Connection failed: {error.GetString()}");
                        }
                    }
                    else
                    {
                        Logger.Error($"[Client {_clientId}] Invalid response format from master server");
                    }
                    break;
                    
                default:
                    Logger.System(LogLevel.Warning, $"[Client {_clientId}] Unhandled message type from master server: {message.Type}");
                    break;
            }
        }

        private void OnMasterServerDisconnected(object sender, EventArgs e)
        {
            Logger.Connection(LogLevel.Warning, $"[Client {_clientId}] Disconnected from master server");
        }

        private async Task ConnectToGameServerAsync()
        {
            try
            {
                var parts = _gameServerEndpoint.Split(':');
                var host = parts[0];
                var port = int.Parse(parts[1]);
                
                Logger.Connection(LogLevel.Debug, $"[Client {_clientId}] Connecting to game server at {host}:{port}...");
                
                _gameServerClient = new SocketClient(host, port);
                _gameServerClient.MessageReceived += OnGameServerMessageReceived;
                _gameServerClient.Disconnected += OnGameServerDisconnected;
                
                await _gameServerClient.ConnectAsync();
                
                // Add a delay after connecting to ensure the connection is stable
                await Task.Delay(200);
                
                Logger.Connection(LogLevel.Debug, $"[Client {_clientId}] Connected to game server");
                
                // Join the game
                await _gameServerClient.SendMessageAsync(Message.Create<object>(MessageType.PlayerJoin, new { ClientId = _clientId }));
                Logger.Connection(LogLevel.Debug, $"[Client {_clientId}] Sent join request to game server");
            }
            catch (Exception ex)
            {
                Logger.Error($"[Client {_clientId}] Failed to connect to game server: {ex.Message}");
                throw;
            }
        }

        private void OnGameServerMessageReceived(object sender, Message message)
        {
            if (message.Type == MessageType.PlayerAction)
            {
                Logger.PlayerAction(LogLevel.Debug, $"[Client {_clientId}] Received player action from game server");
                return;
            }
            
            Logger.System(LogLevel.Debug, $"[Client {_clientId}] Received message from game server: {message.Type}");
            
            switch (message.Type)
            {
                case MessageType.PlayerJoin:
                    try
                    {
                        var response = message.GetData<JsonElement>();
                        if (response.ValueKind == JsonValueKind.Object)
                        {
                            if (response.TryGetProperty("Success", out var success) && success.GetBoolean())
                            {
                                _isConnectedToGameServer = true;
                                Logger.Connection(LogLevel.Info, $"[Client {_clientId}] Successfully joined game");
                                Connected?.Invoke(this, EventArgs.Empty);
                            }
                            else if (response.TryGetProperty("Error", out var error))
                            {
                                Logger.Error($"[Client {_clientId}] Failed to join game: {error.GetString()}");
                            }
                        }
                        else
                        {
                            Logger.Error($"[Client {_clientId}] Invalid response format from game server");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"[Client {_clientId}] Error processing join response: {ex.Message}");
                    }
                    break;
                    
                default:
                    break;
            }
        }

        private void OnGameServerDisconnected(object sender, EventArgs e)
        {
            Logger.Connection(LogLevel.Warning, $"[Client {_clientId}] Disconnected from game server");
            _isConnectedToGameServer = false;
            Disconnected?.Invoke(this, EventArgs.Empty);
        }

        public async Task SendRandomActionAsync()
        {
            if (!_isConnectedToGameServer)
                return;
                
            try
            {
                var actions = new[] { "move", "jump", "attack", "use_item" };
                var action = actions[_random.Next(actions.Length)];
                
                var data = new
                {
                    Action = action,
                    Data = new
                    {
                        X = _random.Next(-100, 100),
                        Y = _random.Next(-100, 100),
                        Z = _random.Next(-100, 100)
                    }
                };
                
                Logger.PlayerAction(LogLevel.Debug, $"[Client {_clientId}] Sent action: {action}");
                ActionSent?.Invoke(this, action);
                
                await _gameServerClient.SendMessageAsync(Message.Create<object>(MessageType.PlayerAction, data));
            }
            catch (Exception ex)
            {
                Logger.Error($"[Client {_clientId}] Error sending action: {ex.Message}");
            }
        }
    }
} 