using System.IO;

namespace SevenZip.Compression.RangeCoder
{
    internal class Encoder
    {
        public const uint KTopValue = 1 << 24;
        private byte cache;
        private uint cacheSize;

        public ulong Low;
        public uint Range;

        private long startPosition;

        private Stream stream;

        public void SetStream(Stream stream)
        {
            this.stream = stream;
        }

        public void ReleaseStream()
        {
            stream = null;
        }

        public void Init()
        {
            startPosition = stream.Position;

            Low = 0;
            Range = 0xFFFFFFFF;
            cacheSize = 1;
            cache = 0;
        }

        public void FlushData()
        {
            for (var i = 0; i < 5; i++)
                ShiftLow();
        }

        public void FlushStream()
        {
            stream.Flush();
        }

        public void CloseStream()
        {
            stream.Close();
        }

        public void Encode(uint start, uint size, uint total)
        {
            Low += start * (Range /= total);
            Range *= size;
            while (Range < KTopValue)
            {
                Range <<= 8;
                ShiftLow();
            }
        }

        public void ShiftLow()
        {
            if ((uint) Low < 0xFF000000 || (uint) (Low >> 32) == 1)
            {
                var temp = cache;
                do
                {
                    stream.WriteByte((byte) (temp + (Low >> 32)));
                    temp = 0xFF;
                } while (--cacheSize != 0);

                cache = (byte) ((uint) Low >> 24);
            }

            cacheSize++;
            Low = (uint) Low << 8;
        }

        public void EncodeDirectBits(uint v, int numTotalBits)
        {
            for (var i = numTotalBits - 1; i >= 0; i--)
            {
                Range >>= 1;
                if (((v >> i) & 1) == 1)
                    Low += Range;
                if (Range >= KTopValue)
                    continue;
                Range <<= 8;
                ShiftLow();
            }
        }

        public void EncodeBit(uint size0, int numTotalBits, uint symbol)
        {
            var newBound = (Range >> numTotalBits) * size0;
            if (symbol == 0)
            {
                Range = newBound;
            }
            else
            {
                Low += newBound;
                Range -= newBound;
            }

            while (Range < KTopValue)
            {
                Range <<= 8;
                ShiftLow();
            }
        }

        public long GetProcessedSizeAdd()
        {
            return cacheSize + stream.Position - startPosition + 4;
        }
    }

    internal class Decoder
    {
        public const uint KTopValue = 1 << 24;
        public uint Code;

        public uint Range;

        public Stream Stream;

        public void Init(Stream stream)
        {
            Stream = stream;

            Code = 0;
            Range = 0xFFFFFFFF;
            for (var i = 0; i < 5; i++)
                Code = (Code << 8) | (byte) Stream.ReadByte();
        }

        public void ReleaseStream()
        {
            Stream = null;
        }

        public void CloseStream()
        {
            Stream.Close();
        }

        public void Normalize()
        {
            while (Range < KTopValue)
            {
                Code = (Code << 8) | (byte) Stream.ReadByte();
                Range <<= 8;
            }
        }

        public void Normalize2()
        {
            if (Range >= KTopValue)
                return;
            Code = (Code << 8) | (byte) Stream.ReadByte();
            Range <<= 8;
        }

        public uint GetThreshold(uint total)
        {
            return Code / (Range /= total);
        }

        public void Decode(uint start, uint size, uint total)
        {
            Code -= start * Range;
            Range *= size;
            Normalize();
        }

        public uint DecodeDirectBits(int numTotalBits)
        {
            var range = Range;
            var code = Code;
            uint result = 0;
            for (var i = numTotalBits; i > 0; i--)
            {
                range >>= 1;
                var t = (code - range) >> 31;
                code -= range & (t - 1);
                result = (result << 1) | (1 - t);

                if (range >= KTopValue)
                    continue;
                code = (code << 8) | (byte) Stream.ReadByte();
                range <<= 8;
            }

            Range = range;
            Code = code;
            return result;
        }

        public uint DecodeBit(uint size0, int numTotalBits)
        {
            var newBound = (Range >> numTotalBits) * size0;
            uint symbol;
            if (Code < newBound)
            {
                symbol = 0;
                Range = newBound;
            }
            else
            {
                symbol = 1;
                Code -= newBound;
                Range -= newBound;
            }

            Normalize();
            return symbol;
        }
    }
}