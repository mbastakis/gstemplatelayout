using System;

namespace Common.Models
{
    public class GameStateUpdate
    {
        public string PlayerId { get; set; }
        public float Position { get; set; }
        public float Rotation { get; set; }
        public float Scale { get; set; }

        public override string ToString()
        {
            return $"GameState for Player {PlayerId?.Substring(0, 6)} - Pos: {Position}, Rot: {Rotation}, Scale: {Scale}";
        }
    }
} 