// Common/CRC.cs

namespace SevenZip
{
    internal class Crc
    {
        public static readonly uint[] Table;

        private uint value = 0xFFFFFFFF;

        static Crc()
        {
            Table = new uint[256];
            const uint kPoly = 0xEDB88320;
            for (uint i = 0; i < 256; i++)
            {
                var r = i;
                for (var j = 0; j < 8; j++)
                    if ((r & 1) != 0)
                        r = (r >> 1) ^ kPoly;
                    else
                        r >>= 1;
                Table[i] = r;
            }
        }

        public void Init()
        {
            value = 0xFFFFFFFF;
        }

        public void UpdateByte(byte b)
        {
            value = Table[(byte) value ^ b] ^ (value >> 8);
        }

        public void Update(byte[] data, uint offset, uint size)
        {
            for (uint i = 0; i < size; i++)
                value = Table[(byte) value ^ data[offset + i]] ^ (value >> 8);
        }

        public uint GetDigest()
        {
            return value ^ 0xFFFFFFFF;
        }
    }
}