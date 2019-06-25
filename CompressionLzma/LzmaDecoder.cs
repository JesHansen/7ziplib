// LzmaDecoder.cs

using System;
using System.IO;
using SevenZip.Compression.LZ;
using SevenZip.Compression.RangeCoder;

namespace SevenZip.Compression.LZMA
{
    public class Decoder : ICoder, ISetDecoderProperties // ,System.IO.Stream
    {
        private uint mDictionarySize;
        private uint mDictionarySizeCheck;

        private readonly BitDecoder[] mIsMatchDecoders = new BitDecoder[Base.KNumStates << Base.KNumPosStatesBitsMax];

        private readonly BitDecoder[] mIsRep0LongDecoders =
            new BitDecoder[Base.KNumStates << Base.KNumPosStatesBitsMax];

        private readonly BitDecoder[] mIsRepDecoders = new BitDecoder[Base.KNumStates];
        private readonly BitDecoder[] mIsRepG0Decoders = new BitDecoder[Base.KNumStates];
        private readonly BitDecoder[] mIsRepG1Decoders = new BitDecoder[Base.KNumStates];
        private readonly BitDecoder[] mIsRepG2Decoders = new BitDecoder[Base.KNumStates];

        private readonly LenDecoder mLenDecoder = new LenDecoder();

        private readonly LiteralDecoder mLiteralDecoder = new LiteralDecoder();

        private readonly OutWindow mOutWindow = new OutWindow();

        private BitTreeDecoder mPosAlignDecoder = new BitTreeDecoder(Base.KNumAlignBits);
        private readonly BitDecoder[] mPosDecoders = new BitDecoder[Base.KNumFullDistances - Base.KEndPosModelIndex];

        private readonly BitTreeDecoder[] mPosSlotDecoder = new BitTreeDecoder[Base.KNumLenToPosStates];

        private uint mPosStateMask;
        private readonly RangeCoder.Decoder mRangeDecoder = new RangeCoder.Decoder();
        private readonly LenDecoder mRepLenDecoder = new LenDecoder();

        private bool solid;

        public Decoder()
        {
            mDictionarySize = 0xFFFFFFFF;
            for (var i = 0; i < Base.KNumLenToPosStates; i++)
                mPosSlotDecoder[i] = new BitTreeDecoder(Base.KNumPosSlotBits);
        }

        public void Code(Stream inStream, Stream outStream,
            long inSize, long outSize, ICodeProgress progress)
        {
            Init(inStream, outStream);

            var state = new Base.State();
            state.Init();
            uint rep0 = 0, rep1 = 0, rep2 = 0, rep3 = 0;

            ulong nowPos64 = 0;
            var outSize64 = (ulong) outSize;
            if (nowPos64 < outSize64)
            {
                if (mIsMatchDecoders[state.Index << Base.KNumPosStatesBitsMax].Decode(mRangeDecoder) != 0)
                    throw new DataErrorException();
                state.UpdateChar();
                var b = mLiteralDecoder.DecodeNormal(mRangeDecoder, 0, 0);
                mOutWindow.PutByte(b);
                nowPos64++;
            }

            while (nowPos64 < outSize64)
            {
                var posState = (uint) nowPos64 & mPosStateMask;
                if (mIsMatchDecoders[(state.Index << Base.KNumPosStatesBitsMax) + posState].Decode(mRangeDecoder) == 0)
                {
                    byte b;
                    var prevByte = mOutWindow.GetByte(0);
                    if (!state.IsCharState())
                        b = mLiteralDecoder.DecodeWithMatchByte(mRangeDecoder,
                            (uint) nowPos64, prevByte, mOutWindow.GetByte(rep0));
                    else
                        b = mLiteralDecoder.DecodeNormal(mRangeDecoder, (uint) nowPos64, prevByte);
                    mOutWindow.PutByte(b);
                    state.UpdateChar();
                    nowPos64++;
                }
                else
                {
                    uint len;
                    if (mIsRepDecoders[state.Index].Decode(mRangeDecoder) == 1)
                    {
                        if (mIsRepG0Decoders[state.Index].Decode(mRangeDecoder) == 0)
                        {
                            if (mIsRep0LongDecoders[(state.Index << Base.KNumPosStatesBitsMax) + posState]
                                    .Decode(mRangeDecoder) == 0)
                            {
                                state.UpdateShortRep();
                                mOutWindow.PutByte(mOutWindow.GetByte(rep0));
                                nowPos64++;
                                continue;
                            }
                        }
                        else
                        {
                            uint distance;
                            if (mIsRepG1Decoders[state.Index].Decode(mRangeDecoder) == 0)
                            {
                                distance = rep1;
                            }
                            else
                            {
                                if (mIsRepG2Decoders[state.Index].Decode(mRangeDecoder) == 0)
                                {
                                    distance = rep2;
                                }
                                else
                                {
                                    distance = rep3;
                                    rep3 = rep2;
                                }

                                rep2 = rep1;
                            }

                            rep1 = rep0;
                            rep0 = distance;
                        }

                        len = mRepLenDecoder.Decode(mRangeDecoder, posState) + Base.KMatchMinLen;
                        state.UpdateRep();
                    }
                    else
                    {
                        rep3 = rep2;
                        rep2 = rep1;
                        rep1 = rep0;
                        len = Base.KMatchMinLen + mLenDecoder.Decode(mRangeDecoder, posState);
                        state.UpdateMatch();
                        var posSlot = mPosSlotDecoder[Base.GetLenToPosState(len)].Decode(mRangeDecoder);
                        if (posSlot >= Base.KStartPosModelIndex)
                        {
                            var numDirectBits = (int) ((posSlot >> 1) - 1);
                            rep0 = (2 | (posSlot & 1)) << numDirectBits;
                            if (posSlot < Base.KEndPosModelIndex)
                            {
                                rep0 += BitTreeDecoder.ReverseDecode(mPosDecoders,
                                    rep0 - posSlot - 1, mRangeDecoder, numDirectBits);
                            }
                            else
                            {
                                rep0 += mRangeDecoder.DecodeDirectBits(
                                            numDirectBits - Base.KNumAlignBits) << Base.KNumAlignBits;
                                rep0 += mPosAlignDecoder.ReverseDecode(mRangeDecoder);
                            }
                        }
                        else
                        {
                            rep0 = posSlot;
                        }
                    }

                    if (rep0 >= mOutWindow.TrainSize + nowPos64 || rep0 >= mDictionarySizeCheck)
                    {
                        if (rep0 == 0xFFFFFFFF)
                            break;
                        throw new DataErrorException();
                    }

                    mOutWindow.CopyBlock(rep0, len);
                    nowPos64 += len;
                }
            }

            mOutWindow.Flush();
            mOutWindow.ReleaseStream();
            mRangeDecoder.ReleaseStream();
        }

        public void SetDecoderProperties(byte[] properties)
        {
            if (properties.Length < 5)
                throw new InvalidParamException();
            var lc = properties[0] % 9;
            var remainder = properties[0] / 9;
            var lp = remainder % 5;
            var pb = remainder / 5;
            if (pb > Base.KNumPosStatesBitsMax)
                throw new InvalidParamException();
            uint dictionarySize = 0;
            for (var i = 0; i < 4; i++)
                dictionarySize += (uint) properties[1 + i] << (i * 8);
            SetDictionarySize(dictionarySize);
            SetLiteralProperties(lp, lc);
            SetPosBitsProperties(pb);
        }

        private void SetDictionarySize(uint dictionarySize)
        {
            if (mDictionarySize != dictionarySize)
            {
                mDictionarySize = dictionarySize;
                mDictionarySizeCheck = Math.Max(mDictionarySize, 1);
                var blockSize = Math.Max(mDictionarySizeCheck, 1 << 12);
                mOutWindow.Create(blockSize);
            }
        }

        private void SetLiteralProperties(int lp, int lc)
        {
            if (lp > 8)
                throw new InvalidParamException();
            if (lc > 8)
                throw new InvalidParamException();
            mLiteralDecoder.Create(lp, lc);
        }

        private void SetPosBitsProperties(int pb)
        {
            if (pb > Base.KNumPosStatesBitsMax)
                throw new InvalidParamException();
            var numPosStates = (uint) 1 << pb;
            mLenDecoder.Create(numPosStates);
            mRepLenDecoder.Create(numPosStates);
            mPosStateMask = numPosStates - 1;
        }

        private void Init(Stream inStream, Stream outStream)
        {
            mRangeDecoder.Init(inStream);
            mOutWindow.Init(outStream, solid);

            uint i;
            for (i = 0; i < Base.KNumStates; i++)
            {
                for (uint j = 0; j <= mPosStateMask; j++)
                {
                    var index = (i << Base.KNumPosStatesBitsMax) + j;
                    mIsMatchDecoders[index].Init();
                    mIsRep0LongDecoders[index].Init();
                }

                mIsRepDecoders[i].Init();
                mIsRepG0Decoders[i].Init();
                mIsRepG1Decoders[i].Init();
                mIsRepG2Decoders[i].Init();
            }

            mLiteralDecoder.Init();
            for (i = 0; i < Base.KNumLenToPosStates; i++)
                mPosSlotDecoder[i].Init();
            for (i = 0; i < Base.KNumFullDistances - Base.KEndPosModelIndex; i++)
                mPosDecoders[i].Init();

            mLenDecoder.Init();
            mRepLenDecoder.Init();
            mPosAlignDecoder.Init();
        }

        public bool Train(Stream stream)
        {
            solid = true;
            return mOutWindow.Train(stream);
        }

        private class LenDecoder
        {
            private BitDecoder mChoice = new BitDecoder();
            private BitDecoder mChoice2 = new BitDecoder();
            private BitTreeDecoder mHighCoder = new BitTreeDecoder(Base.KNumHighLenBits);
            private readonly BitTreeDecoder[] mLowCoder = new BitTreeDecoder[Base.KNumPosStatesMax];
            private readonly BitTreeDecoder[] mMidCoder = new BitTreeDecoder[Base.KNumPosStatesMax];
            private uint mNumPosStates;

            public void Create(uint numPosStates)
            {
                for (var posState = mNumPosStates; posState < numPosStates; posState++)
                {
                    mLowCoder[posState] = new BitTreeDecoder(Base.KNumLowLenBits);
                    mMidCoder[posState] = new BitTreeDecoder(Base.KNumMidLenBits);
                }

                mNumPosStates = numPosStates;
            }

            public void Init()
            {
                mChoice.Init();
                for (uint posState = 0; posState < mNumPosStates; posState++)
                {
                    mLowCoder[posState].Init();
                    mMidCoder[posState].Init();
                }

                mChoice2.Init();
                mHighCoder.Init();
            }

            public uint Decode(RangeCoder.Decoder rangeDecoder, uint posState)
            {
                if (mChoice.Decode(rangeDecoder) == 0)
                {
                    return mLowCoder[posState].Decode(rangeDecoder);
                }

                var symbol = Base.KNumLowLenSymbols;
                if (mChoice2.Decode(rangeDecoder) == 0)
                {
                    symbol += mMidCoder[posState].Decode(rangeDecoder);
                }
                else
                {
                    symbol += Base.KNumMidLenSymbols;
                    symbol += mHighCoder.Decode(rangeDecoder);
                }

                return symbol;
            }
        }

        private class LiteralDecoder
        {
            private Decoder2[] mCoders;
            private int mNumPosBits;
            private int mNumPrevBits;
            private uint mPosMask;

            public void Create(int numPosBits, int numPrevBits)
            {
                if (mCoders != null && mNumPrevBits == numPrevBits &&
                    mNumPosBits == numPosBits)
                    return;
                mNumPosBits = numPosBits;
                mPosMask = ((uint) 1 << numPosBits) - 1;
                mNumPrevBits = numPrevBits;
                var numStates = (uint) 1 << (mNumPrevBits + mNumPosBits);
                mCoders = new Decoder2[numStates];
                for (uint i = 0; i < numStates; i++)
                    mCoders[i].Create();
            }

            public void Init()
            {
                var numStates = (uint) 1 << (mNumPrevBits + mNumPosBits);
                for (uint i = 0; i < numStates; i++)
                    mCoders[i].Init();
            }

            private uint GetState(uint pos, byte prevByte)
            {
                return ((pos & mPosMask) << mNumPrevBits) + (uint) (prevByte >> (8 - mNumPrevBits));
            }

            public byte DecodeNormal(RangeCoder.Decoder rangeDecoder, uint pos, byte prevByte)
            {
                return mCoders[GetState(pos, prevByte)].DecodeNormal(rangeDecoder);
            }

            public byte DecodeWithMatchByte(RangeCoder.Decoder rangeDecoder, uint pos, byte prevByte, byte matchByte)
            {
                return mCoders[GetState(pos, prevByte)].DecodeWithMatchByte(rangeDecoder, matchByte);
            }

            private struct Decoder2
            {
                private BitDecoder[] mDecoders;

                public void Create()
                {
                    mDecoders = new BitDecoder[0x300];
                }

                public void Init()
                {
                    for (var i = 0; i < 0x300; i++) mDecoders[i].Init();
                }

                public byte DecodeNormal(RangeCoder.Decoder rangeDecoder)
                {
                    uint symbol = 1;
                    do
                    {
                        symbol = (symbol << 1) | mDecoders[symbol].Decode(rangeDecoder);
                    } while (symbol < 0x100);

                    return (byte) symbol;
                }

                public byte DecodeWithMatchByte(RangeCoder.Decoder rangeDecoder, byte matchByte)
                {
                    uint symbol = 1;
                    do
                    {
                        var matchBit = (uint) (matchByte >> 7) & 1;
                        matchByte <<= 1;
                        var bit = mDecoders[((1 + matchBit) << 8) + symbol].Decode(rangeDecoder);
                        symbol = (symbol << 1) | bit;
                        if (matchBit != bit)
                        {
                            while (symbol < 0x100)
                                symbol = (symbol << 1) | mDecoders[symbol].Decode(rangeDecoder);
                            break;
                        }
                    } while (symbol < 0x100);

                    return (byte) symbol;
                }
            }
        }
    }
}