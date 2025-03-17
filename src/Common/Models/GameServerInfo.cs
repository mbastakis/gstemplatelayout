using System;

namespace Common.Models
{
    public class GameServerInfo
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Endpoint { get; set; }
        public int CurrentPlayers { get; set; }
        public int MaxPlayers { get; set; } = 100;
        public DateTime LastHeartbeat { get; set; } = DateTime.UtcNow;
        public bool IsAvailable => CurrentPlayers < MaxPlayers;
    }
    
    public class GameServerHeartbeatData
    {
        public string ServerId { get; set; }
        public int CurrentPlayers { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }
    
    public class GameServerRegistrationData
    {
        public string ServerId { get; set; }
        public string Endpoint { get; set; }
        public int MaxPlayers { get; set; }
    }
} 