using System;

namespace Common.Models
{
    // Request from client to join a game server
    public class PlayerJoinRequest
    {
        public string ClientId { get; set; }
        
        public override string ToString()
        {
            return $"Join request from client {ClientId?.Substring(0, 6)}";
        }
    }
    
    // Response from game server for join request
    public class PlayerJoinResponse
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        
        public override string ToString()
        {
            return Success 
                ? "Join successful" 
                : $"Join failed: {Error}";
        }
    }
    
    // Notification to other players that someone joined
    public class PlayerJoinNotification
    {
        public string PlayerId { get; set; }
        
        public override string ToString()
        {
            return $"Player {PlayerId?.Substring(0, 6)} joined";
        }
    }
    
    // Notification that a player left
    public class PlayerLeaveNotification
    {
        public string PlayerId { get; set; }
        
        public override string ToString()
        {
            return $"Player {PlayerId?.Substring(0, 6)} left";
        }
    }
    
    // Player data update sent to clients
    public class PlayerUpdateMessage
    {
        public string PlayerId { get; set; }
        public float Position { get; set; }
        public float Rotation { get; set; }
        public float Scale { get; set; }
        
        public override string ToString()
        {
            return $"Update for player {PlayerId?.Substring(0, 6)} - Pos: {Position}, Rot: {Rotation}, Scale: {Scale}";
        }
    }
} 