// BoomNetwork TowerDefense Demo — Input Encoding
//
// 4-byte input: [byte GridX] [byte GridY] [byte TowerType] [byte reserved]
// TowerType = 0 → no-op (Silent When Idle — not sent)
// TowerType = 1/2/3 → place Arrow/Cannon/Magic
// TowerType = SellAction (4) → sell tower at (GridX, GridY)

namespace BoomNetwork.Samples.TowerDefense
{
    public static class TDInput
    {
        public const int InputSize = 4;
        public const byte SellAction    = 4;
        public const byte UpgradeAction = 5;

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
