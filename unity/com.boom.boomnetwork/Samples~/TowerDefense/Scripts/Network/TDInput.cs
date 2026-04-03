// BoomNetwork TowerDefense Demo — Input Encoding
//
// 4-byte input: [byte GridX] [byte GridY] [byte Action] [byte reserved]
// Action = 0              → no-op (Silent When Idle — not sent)
// Action = 1-7            → place tower of that TowerType
// Action = 10             → sell tower at (GridX, GridY)
// Action = 11             → upgrade tower at (GridX, GridY)
// Action = 12             → speed change (GridX = speed mode 0-3)
// Action = 13             → start next wave immediately
//
// IMPORTANT: action byte values 1-7 map to TowerType enum values.
// All special actions MUST use values > 7 to avoid collision.

namespace BoomNetwork.Samples.TowerDefense
{
    public static class TDInput
    {
        public const int InputSize = 4;
        public const byte SellAction      = 10;
        public const byte UpgradeAction   = 11;
        public const byte SpeedAction     = 12; // gx = speed mode (0-3)
        public const byte StartWaveAction = 13; // trigger next wave immediately
        public const byte RestartAction   = 14; // restart the game (any player)

        // Speed mode values (used as gx payload for SpeedAction)
        public const byte SpeedSlow   = 0; // 0.25x — tactical pause
        public const byte SpeedNormal = 1; // 1x
        public const byte Speed2x     = 2;
        public const byte Speed3x     = 3;

        public static void Encode(byte[] buf, int gridX, int gridY, byte towerTypeByte)
        {
            buf[0] = (byte)gridX;
            buf[1] = (byte)gridY;
            buf[2] = towerTypeByte;
            buf[3] = 0;
        }

        public static void Decode(byte[] buf, int offset, out int gridX, out int gridY, out TowerType towerType)
        {
            gridX      = buf[offset];
            gridY      = buf[offset + 1];
            towerType  = (TowerType)buf[offset + 2];
        }
    }
}
