namespace SevenZip.Compression.RangeCoder
{
    internal struct BitEncoder
    {
        public const int KNumBitModelTotalBits = 11;
        public const uint KBitModelTotal = 1 << KNumBitModelTotalBits;
        private const int KNumMoveBits = 5;
        private const int KNumMoveReducingBits = 2;
        public const int KNumBitPriceShiftBits = 6;

        private uint prob;

        public void Init()
        {
            prob = KBitModelTotal >> 1;
        }

        public void UpdateModel(uint symbol)
        {
            if (symbol == 0)
                prob += (KBitModelTotal - prob) >> KNumMoveBits;
            else
                prob -= prob >> KNumMoveBits;
        }

        public void Encode(Encoder encoder, uint symbol)
        {
            var newBound = (encoder.Range >> KNumBitModelTotalBits) * prob;
            if (symbol == 0)
            {
                encoder.Range = newBound;
                prob += (KBitModelTotal - prob) >> KNumMoveBits;
            }
            else
            {
                encoder.Low += newBound;
                encoder.Range -= newBound;
                prob -= prob >> KNumMoveBits;
            }

            if (encoder.Range >= Encoder.KTopValue)
                return;
            encoder.Range <<= 8;
            encoder.ShiftLow();
        }

        private static readonly uint[] _probPrices = new uint[KBitModelTotal >> KNumMoveReducingBits];

        static BitEncoder()
        {
            const int kNumBits = KNumBitModelTotalBits - KNumMoveReducingBits;
            for (var i = kNumBits - 1; i >= 0; i--)
            {
                var start = (uint) 1 << (kNumBits - i - 1);
                var end = (uint) 1 << (kNumBits - i);
                for (var j = start; j < end; j++)
                    _probPrices[j] = ((uint) i << KNumBitPriceShiftBits) +
                                     (((end - j) << KNumBitPriceShiftBits) >> (kNumBits - i - 1));
            }
        }

        public uint GetPrice(uint symbol)
        {
            return _probPrices[(((prob - symbol) ^ -(int) symbol) & (KBitModelTotal - 1)) >> KNumMoveReducingBits];
        }

        public uint GetPrice0()
        {
            return _probPrices[prob >> KNumMoveReducingBits];
        }

        public uint GetPrice1()
        {
            return _probPrices[(KBitModelTotal - prob) >> KNumMoveReducingBits];
        }
    }

    internal struct BitDecoder
    {
        public const int KNumBitModelTotalBits = 11;
        public const uint KBitModelTotal = 1 << KNumBitModelTotalBits;
        private const int KNumMoveBits = 5;

        private uint prob;

        public void UpdateModel(int numMoveBits, uint symbol)
        {
            if (symbol == 0)
                prob += (KBitModelTotal - prob) >> numMoveBits;
            else
                prob -= prob >> numMoveBits;
        }

        public void Init()
        {
            prob = KBitModelTotal >> 1;
        }

        public uint Decode(Decoder rangeDecoder)
        {
            var newBound = (rangeDecoder.Range >> KNumBitModelTotalBits) * prob;
            if (rangeDecoder.Code < newBound)
            {
                rangeDecoder.Range = newBound;
                prob += (KBitModelTotal - prob) >> KNumMoveBits;
                if (rangeDecoder.Range >= Decoder.KTopValue)
                    return 0;
                rangeDecoder.Code = (rangeDecoder.Code << 8) | (byte) rangeDecoder.Stream.ReadByte();
                rangeDecoder.Range <<= 8;

                return 0;
            }

            rangeDecoder.Range -= newBound;
            rangeDecoder.Code -= newBound;
            prob -= prob >> KNumMoveBits;
            if (rangeDecoder.Range >= Decoder.KTopValue)
                return 1;
            rangeDecoder.Code = (rangeDecoder.Code << 8) | (byte) rangeDecoder.Stream.ReadByte();
            rangeDecoder.Range <<= 8;

            return 1;
        }
    }
}