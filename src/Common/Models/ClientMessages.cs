using System;

namespace Common.Models
{
    // Request from client to connect to a game server (sent to master server)
    public class ClientConnectRequest
    {
        public string ClientId { get; set; }
        
        public override string ToString()
        {
            return $"Connection request from client {ClientId?.Substring(0, 6)}";
        }
    }
    
    // Response from master server for connection request
    public class ClientConnectResponse
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public string ServerEndpoint { get; set; }
        
        public override string ToString()
        {
            return Success 
                ? $"Connection successful, assigned to: {ServerEndpoint}" 
                : $"Connection failed: {Error}";
        }
    }
    
    // Notification that a client disconnected
    public class ClientDisconnectNotification
    {
        public string ClientId { get; set; }
        
        public override string ToString()
        {
            return $"Client {ClientId?.Substring(0, 6)} disconnected";
        }
    }
} 