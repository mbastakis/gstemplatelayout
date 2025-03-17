using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using Common.Logging;

namespace ClientSimulator
{
    class Program
    {
        private const string DefaultMasterHost = "localhost";
        private const int DefaultMasterPort = 7000;
        private const int DefaultNumClients = 10;
        private const int DefaultActionsPerSecond = 5;
        
        static async Task Main(string[] args)
        {
            // Create logs directory if it doesn't exist
            Directory.CreateDirectory("logs");
            
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
        
        private static string GetRandomAction(Random random)
        {
            string[] actions = { "move", "jump", "attack", "defend", "use_item" };
            return actions[random.Next(actions.Length)];
        }
    }
} 