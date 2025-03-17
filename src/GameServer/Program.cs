using System;
using System.IO;
using System.Threading.Tasks;
using Common.Logging;

namespace GameServer
{
    class Program
    {
        private const int DefaultPort = 7100;
        private const string DefaultMasterHost = "localhost";
        private const int DefaultMasterPort = 7000;
        private const int DefaultMaxPlayers = 100;
        
        static async Task Main(string[] args)
        {
            // Create logs directory if it doesn't exist
            Directory.CreateDirectory("logs");
            
            int port = DefaultPort;
            string masterHost = DefaultMasterHost;
            int masterPort = DefaultMasterPort;
            int maxPlayers = DefaultMaxPlayers;
            
            // Parse command line arguments
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--port" && i + 1 < args.Length)
                {
                    if (int.TryParse(args[i + 1], out int customPort))
                    {
                        port = customPort;
                    }
                }
                else if (args[i] == "--master-host" && i + 1 < args.Length)
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
                else if (args[i] == "--max-players" && i + 1 < args.Length)
                {
                    if (int.TryParse(args[i + 1], out int customMaxPlayers))
                    {
                        maxPlayers = customMaxPlayers;
                    }
                }
            }
            
            var server = new GameServer(port, masterHost, masterPort, maxPlayers);
            
            // Handle Ctrl+C
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                Logger.System(LogLevel.Info, "Shutdown signal received");
                server.Stop();
                Logger.Close();
            };
            
            try
            {
                await server.Start();
                Logger.System(LogLevel.Info, $"Game Server running on port {port}. Press Ctrl+C to stop.");
                
                // Wait for the server to be stopped
                var tcs = new TaskCompletionSource<bool>();
                Console.CancelKeyPress += (sender, e) => tcs.TrySetResult(true);
                await tcs.Task;
            }
            catch (Exception ex)
            {
                Logger.Error("Error starting server", ex);
                server.Stop();
                Logger.Close();
                Environment.Exit(1);
            }
        }
    }
} 