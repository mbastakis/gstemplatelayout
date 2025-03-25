using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Common.Logging;
using Common.Models;
using Common.Networking;
using System.Text.Json;

namespace ClientSimulator
{
    public class ClientSimulator
    {
        private readonly string _masterServerHost;
        private readonly int _masterServerPort;
        private readonly int _numClients;
        private readonly int _actionsPerSecond;
        private readonly List<Common.Networking.GameClient> _clients = new List<Common.Networking.GameClient>();
        private readonly Random _random = new Random();
        private CancellationTokenSource _cancellationTokenSource;
        private int _connectedClients;  // Initialize to 0
        private bool _isShuttingDown = false;
        private readonly object _lock = new object();

        public ClientSimulator(string masterServerHost, int masterServerPort, int numClients, int actionsPerSecond)
        {
            _masterServerHost = masterServerHost;
            _masterServerPort = masterServerPort;
            _numClients = numClients;
            _actionsPerSecond = actionsPerSecond;
            _connectedClients = 0;  // Explicitly initialize to 0
            
            Logger.Initialize("ClientSimulator", "logs/client-simulator.log");
        }

        public async Task Start()
        {
            _cancellationTokenSource = new CancellationTokenSource();
            var token = _cancellationTokenSource.Token;
            
            // Reset connected client counter at start
            _connectedClients = 0;
            Logger.System(LogLevel.Info, $"Starting {_numClients} simulated clients connecting to {_masterServerHost}:{_masterServerPort}");
            
            // Create and start clients
            for (int i = 0; i < _numClients; i++)
            {
                var client = new Common.Networking.GameClient(_masterServerHost, _masterServerPort);
                _clients.Add(client);
                
                // Only connect Connected event handler - this will fire when connection to Game Server is complete
                client.Connected += OnClientConnected;
                client.Disconnected += OnClientDisconnected;
                client.ActionSent += OnClientActionSent;
                
                try
                {
                    // This initiates connection to Master Server first, which will then route to Game Server
                    await client.ConnectAsync();
                }
                catch (Exception ex)
                {
                    Logger.Error($"Client failed to connect: {ex.Message}");
                }
                
                // Add a small delay between client connections to avoid overwhelming the server
                await Task.Delay(100);
            }
            
            Logger.System(LogLevel.Info, $"Created {_clients.Count} clients. Now waiting for connections to game servers.");
            
            // Wait for all clients to connect to Game Servers or timeout
            int timeoutSeconds = 30;
            for (int i = 0; i < timeoutSeconds && _connectedClients < _numClients; i++)
            {
                Logger.Connection(LogLevel.Info, $"Waiting for clients to connect to game servers... {_connectedClients}/{_numClients}");
                
                // Special check to exit the loop early if all clients are connected
                if (_connectedClients >= _numClients)
                {
                    Logger.Connection(LogLevel.Info, $"All clients connected to game servers! Breaking wait loop early.");
                    break;
                }
                
                await Task.Delay(1000);
            }
            
            Logger.Connection(LogLevel.Info, $"Clients connected to game servers: {_connectedClients}/{_numClients}");
            
            if (_connectedClients == 0)
            {
                Logger.Error("No clients were able to connect to game servers. Exiting.");
                return;
            }
            
            // Start sending actions
            Logger.System(LogLevel.Info, $"Starting to send {_actionsPerSecond} actions per second");
            
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // Check if we have connected clients
                    if (_connectedClients == 0)
                    {
                        Logger.System(LogLevel.Warning, "No connected clients, waiting...");
                        await Task.Delay(1000);
                        continue;
                    }
                    
                    // Select random connected clients to send actions
                    var connectedClients = _clients.Where(c => c.IsConnected).ToList();
                    
                    if (connectedClients.Count == 0)
                    {
                        Logger.System(LogLevel.Warning, "No connected clients found, waiting...");
                        await Task.Delay(1000);
                        continue;
                    }
                    
                    Logger.System(LogLevel.Debug, $"Sending actions with {connectedClients.Count} connected clients");
                    int numActionsToSend = Math.Min(_actionsPerSecond, connectedClients.Count);
                    
                    for (int i = 0; i < numActionsToSend; i++)
                    {
                        var client = connectedClients[_random.Next(connectedClients.Count)];
                        await client.SendRandomActionAsync();
                    }
                    
                    await Task.Delay(1000);
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error in action loop: {ex.Message}");
                    await Task.Delay(1000);
                }
            }
        }

        public async Task Stop()
        {
            if (_isShuttingDown)
                return;
                
            _isShuttingDown = true;
            Logger.System(LogLevel.Info, "Shutting down clients...");
            _cancellationTokenSource?.Cancel();
            
            // Disconnect clients in parallel but with a small delay between each to avoid overwhelming the server
            foreach (var client in _clients)
            {
                try
                {
                    client.Disconnect();
                    await Task.Delay(50); // Small delay between disconnections
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error disconnecting client: {ex.Message}");
                }
            }
            
            Logger.System(LogLevel.Info, "All clients stopped.");
        }
        
        private void OnClientConnected(object sender, EventArgs e)
        {
            var client = sender as Common.Networking.GameClient;
            var clientId = client?.ClientId ?? "unknown";
            
            // Create structured logging data
            var logProps = new Dictionary<string, object>
            {
                { "ClientId", clientId },
                { "Action", "connected" },
                { "Component", "ClientSimulator" }
            };
            
            // Interlocked.Increment returns the new value
            var newCount = Interlocked.Increment(ref _connectedClients);
            
            // Make sure we don't exceed the number of clients
            if (newCount > _numClients)
            {
                Interlocked.CompareExchange(ref _connectedClients, _numClients, newCount);
                newCount = _numClients;
            }
            
            // Add connected count to properties
            logProps["ConnectedCount"] = newCount;
            logProps["TotalClients"] = _numClients;
            
            // Use the new structured logging format
            Logger.Connection(LogLevel.Info, $"Client {clientId.Substring(0, 6)} connected to game server. Total connected: {newCount}/{_numClients}", logProps);
        }
        
        private void OnClientDisconnected(object sender, EventArgs e)
        {
            var client = sender as Common.Networking.GameClient;
            var clientId = client?.ClientId ?? "unknown";
            
            // Structured logging data
            var logProps = new Dictionary<string, object>
            {
                { "ClientId", clientId },
                { "Action", "disconnected" },
                { "Component", "ClientSimulator" }
            };
            
            var newCount = Interlocked.Decrement(ref _connectedClients);
            
            // Add connected count to properties
            logProps["ConnectedCount"] = newCount;
            logProps["TotalClients"] = _numClients;
            
            Logger.Connection(LogLevel.Info, $"Client {clientId.Substring(0, 6)} disconnected from game server. Total connected: {newCount}/{_numClients}", logProps);
        }
        
        private void OnClientActionSent(object sender, string action)
        {
            var client = sender as Common.Networking.GameClient;
            var clientId = client?.ClientId ?? "unknown";
            
            // Structured logging with more context
            var logProps = new Dictionary<string, object>
            {
                { "ClientId", clientId },
                { "ActionType", action },
                { "Component", "ClientSimulator" }
            };
            
            Logger.PlayerAction(LogLevel.Info, $"Client {clientId.Substring(0, 6)} sent action: {action}", logProps);
        }

        private async Task SendRandomActionAsync(SocketClient client, string clientId)
        {
            var action = new PlayerAction
            {
                Type = (ActionType)_random.Next(0, 3),
                Value = _random.Next(0, 100)
            };

            var message = new Message
            {
                Type = MessageType.PlayerAction,
                Data = JsonSerializer.Serialize(action)
            };

            Logger.PlayerAction(LogLevel.Debug, $"Sending action from client {clientId.Substring(0, 6)}: {action.Type} = {action.Value}");
            await client.SendMessageAsync(message);
        }
    }
} 