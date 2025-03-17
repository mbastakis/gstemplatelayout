using System;
using System.IO;
using System.Threading.Tasks;
using Common.Logging;

namespace MasterServer
{
    class Program
    {
        private const int DefaultPort = 7000;
        
        static async Task Main(string[] args)
        {
            // Create logs directory if it doesn't exist
            Directory.CreateDirectory("logs");
            
            int port = DefaultPort;
            
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
            }
            
            var server = new MasterServer(port);
            
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
                Logger.System(LogLevel.Info, "Master Server running. Press Ctrl+C to stop.");
                
                // Wait for Ctrl+C
                var tcs = new TaskCompletionSource<bool>();
                Console.CancelKeyPress += (sender, e) => tcs.TrySetResult(true);
                await tcs.Task;
            }
            catch (Exception ex)
            {
                Logger.Error("Error starting server", ex);
                server.Stop();
                Logger.Close();
            }
        }
    }
} 