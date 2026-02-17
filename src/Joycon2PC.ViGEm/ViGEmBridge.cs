using System;
using Joycon2PC.Core;

namespace Joycon2PC.ViGEm
{
    public class ViGEmBridge
    {
        public ViGEmBridge()
        {
        }

        public void Connect()
        {
            // TODO: integrate ViGEmClient to create a virtual XInput device
            Console.WriteLine("[ViGEmBridge] Connect (stub)");
        }

        // Apply a parsed state to the virtual controller (stub)
        public void UpdateFromState(JoyconState state)
        {
            // Map `state` to XInput fields and call ViGEm update API here.
            Console.WriteLine($"[ViGEmBridge] Update - Buttons=0x{state.Buttons:X4} LX={state.LeftStickX} LY={state.LeftStickY}");
        }
    }
}
