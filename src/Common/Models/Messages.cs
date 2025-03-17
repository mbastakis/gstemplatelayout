using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Common.Logging;

namespace Common.Models
{
    public enum MessageType
    {
        // Client to Master messages
        ClientConnect,
        ClientDisconnect,
        
        // Master to GameServer messages
        RegisterGameServer,
        GameServerStatus,
        
        // Client to GameServer messages
        PlayerJoin,
        PlayerLeave,
        PlayerAction,
        
        // GameServer to Client messages
        PlayerUpdate,
        
        // GameServer to Master messages
        GameServerHeartbeat,
        GameServerPlayerCount
    }

    public class Message
    {
        [JsonPropertyName("type")]
        public MessageType Type { get; set; }
        
        [JsonPropertyName("data")]
        public string Data { get; set; }
        
        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        
        public T GetData<T>()
        {
            if (string.IsNullOrEmpty(Data))
                return default;
            
            try
            {
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };
                
                if (typeof(T) == typeof(JsonElement) || typeof(T) == typeof(object))
                {
                    // For dynamic/JsonElement requests, parse using JsonDocument to handle null values better
                    using var doc = JsonDocument.Parse(Data);
                    return JsonSerializer.Deserialize<T>(Data, options);
                }
                
                return JsonSerializer.Deserialize<T>(Data, options);
            }
            catch (JsonException ex)
            {
                Logger.Error($"Error deserializing message data: {ex.Message}");
                return default;
            }
        }
        
        public static Message Create<T>(MessageType type, T data)
        {
            string serializedData = null;
            
            if (data != null)
            {
                try
                {
                    serializedData = JsonSerializer.Serialize(data);
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error serializing message data: {ex.Message}");
                }
            }
            
            return new Message
            {
                Type = type,
                Data = serializedData,
                Timestamp = DateTime.UtcNow
            };
        }
        
        public static string Serialize(Message message)
        {
            try
            {
                return JsonSerializer.Serialize(message);
            }
            catch (Exception ex)
            {
                Logger.Error($"Error serializing message: {ex.Message}");
                return null;
            }
        }
        
        public static Message Deserialize(string json)
        {
            try
            {
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };
                
                return JsonSerializer.Deserialize<Message>(json, options);
            }
            catch (Exception ex)
            {
                Logger.Error($"Error deserializing message: {ex.Message}");
                return null;
            }
        }
    }
} 