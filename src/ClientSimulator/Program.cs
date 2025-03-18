using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using Common.Logging;
using System.Net.Sockets;
using System.Net;

namespace ClientSimulator
{
    class Program
    {
        private const string DefaultMasterHost = "localhost";
        private const int DefaultMasterPort = 7000;
        private const int DefaultNumClients = 10;
        private const int DefaultActionsPerSecond = 1;
        
        static async Task Main(string[] args)
        {
            // Create logs directory if it doesn't exist
            Directory.CreateDirectory("logs");
            
            // Initialize logging
            Logger.Initialize("ClientSimulator", "logs/client-simulator.log");
            
            // Run connectivity test first
            await TestDirectConnection("game-server", 7100);
            
            // Try to run a test connection to master server that matches the Message protocol
            await TestMasterServerConnection("master-server", 7000);
            
            string masterHost = DefaultMasterHost;
            int masterPort = DefaultMasterPort;
            int numClients = DefaultNumClients;
            int actionsPerSecond = DefaultActionsPerSecond;
            
            // Parse command line arguments
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--master-host" && i + 1 < args.Length)
                {
                    masterHost = args[i + 1];
                }
                else if (args[i] == "--master-port" && i + 1 < args.Length)
                {
                    if (int.TryParse(args[i + 1], out int customMasterPort))
                    {
                        masterPort = customMasterPort;
                    }
                }
                else if (args[i] == "--num-clients" && i + 1 < args.Length)
                {
                    if (int.TryParse(args[i + 1], out int customNumClients))
                    {
                        numClients = customNumClients;
                    }
                }
                else if (args[i] == "--actions-per-second" && i + 1 < args.Length)
                {
                    if (int.TryParse(args[i + 1], out int customActionsPerSecond))
                    {
                        actionsPerSecond = customActionsPerSecond;
                    }
                }
            }
            
            var simulator = new ClientSimulator(masterHost, masterPort, numClients, actionsPerSecond);
            
            Console.CancelKeyPress += async (sender, e) =>
            {
                e.Cancel = true;
                Logger.System(LogLevel.Info, "Shutdown signal received");
                await simulator.Stop();
                Logger.Close();
            };
            
            try 
            {
                Logger.System(LogLevel.Info, $"Starting client simulator with {numClients} clients.");
                Logger.System(LogLevel.Info, $"Master server: {masterHost}:{masterPort}");
                Logger.System(LogLevel.Info, $"Actions per second: {actionsPerSecond}");
                
                await simulator.Start();
                
                // Wait for Ctrl+C
                var tcs = new TaskCompletionSource<bool>();
                Console.CancelKeyPress += (sender, e) => tcs.TrySetResult(true);
                await tcs.Task;
            }
            catch (Exception ex)
            {
                Logger.Error("Error starting client simulator", ex);
            }
            finally
            {
                await simulator.Stop();
                Logger.Close();
            }
        }
        
        private static async Task TestDirectConnection(string host, int port)
        {
            Logger.System(LogLevel.Info, "*** CONNECTIVITY TEST ***");
            
            // First test DNS resolution
            try {
                Logger.System(LogLevel.Info, $"Attempting to resolve host: {host}");
                var addresses = await Dns.GetHostAddressesAsync(host);
                var addressList = string.Join(", ", addresses.Select(a => a.ToString()));
                Logger.System(LogLevel.Info, $"Successfully resolved {host} to: {addressList}");
            }
            catch (Exception ex) {
                Logger.Error($"DNS resolution failed for {host}: {ex.Message}");
            }
            
            // Then test direct TCP connection
            try {
                Logger.System(LogLevel.Info, $"Attempting direct TCP connection to {host}:{port}");
                using (var client = new TcpClient())
                {
                    var connectTask = client.ConnectAsync(host, port);
                    var timeoutTask = Task.Delay(5000); // 5 second timeout
                    var completedTask = await Task.WhenAny(connectTask, timeoutTask);
                    
                    if (completedTask == timeoutTask)
                    {
                        Logger.Error($"Connection to {host}:{port} timed out after 5 seconds");
                    }
                    else if (client.Connected)
                    {
                        Logger.System(LogLevel.Info, $"Successfully connected to {host}:{port}");
                        client.Close();
                    }
                    else
                    {
                        Logger.Error($"Failed to connect to {host}:{port} (no exception but not connected)");
                    }
                }
            }
            catch (Exception ex) {
                Logger.Error($"Connection to {host}:{port} failed: {ex.Message}");
            }
            
            Logger.System(LogLevel.Info, "*** END CONNECTIVITY TEST ***");
        }
        
        private static async Task TestMasterServerConnection(string host, int port)
        {
            Logger.System(LogLevel.Info, "*** MASTER SERVER TEST ***");
            
            try {
                Logger.System(LogLevel.Info, $"Attempting direct connection to master server at {host}:{port}");
                using (var client = new TcpClient())
                {
                    await client.ConnectAsync(host, port);
                    Logger.System(LogLevel.Info, $"Connected to master server at {host}:{port}");
                    
                    var stream = client.GetStream();
                    
                    // Create a sample ClientConnect message with minimal properties
                    var clientId = Guid.NewGuid().ToString().Substring(0, 6);
                    var simpleMessage = new { 
                        type = "ClientConnect", 
                        data = System.Text.Json.JsonSerializer.Serialize(new { ClientId = clientId })
                    };
                    
                    var rawJson = System.Text.Json.JsonSerializer.Serialize(simpleMessage);
                    Logger.System(LogLevel.Info, $"Sending minimal test message to master server: {rawJson}");
                    
                    // Add the message delimiter
                    rawJson += "\n";
                    var messageBytes = System.Text.Encoding.UTF8.GetBytes(rawJson);
                    
                    await stream.WriteAsync(messageBytes, 0, messageBytes.Length);
                    Logger.System(LogLevel.Info, $"Test message sent successfully");
                    
                    // Wait for a response
                    var buffer = new byte[4096];
                    var responseTask = stream.ReadAsync(buffer, 0, buffer.Length);
                    var timeoutTask = Task.Delay(2000);
                    
                    var completedTask = await Task.WhenAny(responseTask, timeoutTask);
                    if (completedTask == responseTask)
                    {
                        var bytesRead = await responseTask;
                        var response = System.Text.Encoding.UTF8.GetString(buffer, 0, bytesRead);
                        Logger.System(LogLevel.Info, $"Received response from master server: {response}");
                    }
                    else
                    {
                        Logger.System(LogLevel.Info, "No response received from master server within timeout");
                    }
                    
                    Logger.System(LogLevel.Info, "Master server connectivity test completed");
                }
            }
            catch (Exception ex) {
                Logger.Error($"Master server connection test failed: {ex.Message}");
            }
            
            Logger.System(LogLevel.Info, "*** END MASTER SERVER TEST ***");
        }
        
        private static string GetRandomAction(Random random)
        {
            string[] actions = { "move", "jump", "attack", "defend", "use_item" };
            return actions[random.Next(actions.Length)];
        }
    }
} 