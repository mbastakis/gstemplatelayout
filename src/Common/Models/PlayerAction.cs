using System;

namespace Common.Models
{
    public enum ActionType
    {
        Move,
        Rotate,
        Scale
    }

    public class PlayerAction
    {
        public ActionType Type { get; set; }
        public float Value { get; set; }

        public override string ToString()
        {
            return $"{Type} = {Value}";
        }
    }
} 