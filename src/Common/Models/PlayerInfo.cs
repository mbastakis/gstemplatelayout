using System;

namespace Common.Models
{
    public class PlayerInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public float Position { get; set; }
        public float Rotation { get; set; }
        public float Scale { get; set; }
        public DateTime JoinTime { get; set; }

        public override string ToString()
        {
            return $"Player {Id?.Substring(0, 6)} - Pos: {Position}, Rot: {Rotation}, Scale: {Scale}";
        }
    }
} 