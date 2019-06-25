// LzmaEncoder.cs

using System;
using System.IO;
using SevenZip.Compression.LZ;
using SevenZip.Compression.RangeCoder;

namespace SevenZip.Compression.LZMA
{
    public class Encoder : ICoder, ISetCoderProperties, IWriteCoderProperties
    {
        private const uint KInfinityPrice = 0xFFFFFFF;

        private const int KDefaultDictionaryLogSize = 22;
        private const uint KNumFastBytesDefault = 0x20;

        private const uint KNumOpts = 1 << 12;

        private const int KPropSize = 5;

        private static readonly byte[] _gFastPos = new byte[1 << 11];


        private static readonly string[] _kMatchFinderIDs =
        {
            "BT2",
            "BT4"
        };

        private uint additionalOffset;
        private uint alignPriceCount;
        private readonly uint[] alignPrices = new uint[Base.KAlignTableSize];

        private uint dictionarySize = 1 << KDefaultDictionaryLogSize;
        private uint dictionarySizePrev = 0xFFFFFFFF;
        private readonly uint[] distancesPrices = new uint[Base.KNumFullDistances << Base.KNumLenToPosStatesBits];

        private uint distTableSize = KDefaultDictionaryLogSize * 2;
        private bool finished;
        private Stream inStream;

        private readonly BitEncoder[] isMatch = new BitEncoder[Base.KNumStates << Base.KNumPosStatesBitsMax];
        private readonly BitEncoder[] isRep = new BitEncoder[Base.KNumStates];
        private readonly BitEncoder[] isRep0Long = new BitEncoder[Base.KNumStates << Base.KNumPosStatesBitsMax];
        private readonly BitEncoder[] isRepG0 = new BitEncoder[Base.KNumStates];
        private readonly BitEncoder[] isRepG1 = new BitEncoder[Base.KNumStates];
        private readonly BitEncoder[] isRepG2 = new BitEncoder[Base.KNumStates];

        private readonly LenPriceTableEncoder lenEncoder = new LenPriceTableEncoder();

        private readonly LiteralEncoder literalEncoder = new LiteralEncoder();
        private uint longestMatchLength;

        private bool longestMatchWasFound;

        private readonly uint[] matchDistances = new uint[Base.KMatchMaxLen * 2 + 2];
        private IMatchFinder matchFinder;

        private EMatchFinderType matchFinderType = EMatchFinderType.Bt4;
        private uint matchPriceCount;

        private bool needReleaseMfStream;

        private long nowPos64;
        private uint numDistancePairs;

        private uint numFastBytes = KNumFastBytesDefault;
        private uint numFastBytesPrev = 0xFFFFFFFF;
        private int numLiteralContextBits = 3;
        private int numLiteralPosStateBits;

        private readonly Optimal[] optimum = new Optimal[KNumOpts];
        private uint optimumCurrentIndex;

        private uint optimumEndIndex;
        private BitTreeEncoder posAlignEncoder = new BitTreeEncoder(Base.KNumAlignBits);

        private readonly BitEncoder[] posEncoders = new BitEncoder[Base.KNumFullDistances - Base.KEndPosModelIndex];

        private readonly BitTreeEncoder[] posSlotEncoder = new BitTreeEncoder[Base.KNumLenToPosStates];

        private readonly uint[] posSlotPrices = new uint[1 << (Base.KNumPosSlotBits + Base.KNumLenToPosStatesBits)];

        private int posStateBits = 2;
        private uint posStateMask = 4 - 1;
        private byte previousByte;
        private readonly byte[] properties = new byte[KPropSize];
        private readonly RangeCoder.Encoder rangeEncoder = new RangeCoder.Encoder();
        private readonly uint[] repDistances = new uint[Base.KNumRepDistances];
        private readonly uint[] repLens = new uint[Base.KNumRepDistances];
        private readonly LenPriceTableEncoder repMatchLenEncoder = new LenPriceTableEncoder();

        private readonly uint[] reps = new uint[Base.KNumRepDistances];

        private Base.State state = new Base.State();

        private readonly uint[] tempPrices = new uint[Base.KNumFullDistances];

        private uint trainSize;
        private bool writeEndMark;

        static Encoder()
        {
            const byte kFastSlots = 22;
            var c = 2;
            _gFastPos[0] = 0;
            _gFastPos[1] = 1;
            for (byte slotFast = 2; slotFast < kFastSlots; slotFast++)
            {
                var k = (uint) 1 << ((slotFast >> 1) - 1);
                for (uint j = 0; j < k; j++, c++)
                    _gFastPos[c] = slotFast;
            }
        }

        public Encoder()
        {
            for (var i = 0; i < KNumOpts; i++)
                optimum[i] = new Optimal();
            for (var i = 0; i < Base.KNumLenToPosStates; i++)
                posSlotEncoder[i] = new BitTreeEncoder(Base.KNumPosSlotBits);
        }


        public void Code(Stream inStream, Stream outStream,
            long inSize, long outSize, ICodeProgress progress)
        {
            needReleaseMfStream = false;
            try
            {
                SetStreams(inStream, outStream, inSize, outSize);
                while (true)
                {
                    long processedInSize;
                    long processedOutSize;
                    bool finished;
                    CodeOneBlock(out processedInSize, out processedOutSize, out finished);
                    if (finished)
                        return;
                    progress?.SetProgress(processedInSize, processedOutSize);
                }
            }
            finally
            {
                ReleaseStreams();
            }
        }

        public void SetCoderProperties(CoderPropId[] propIDs, object[] properties)
        {
            for (uint i = 0; i < properties.Length; i++)
            {
                var prop = properties[i];
                switch (propIDs[i])
                {
                    case CoderPropId.NumFastBytes:
                    {
                        if (!(prop is int))
                            throw new InvalidParamException();
                        var numFastBytes = (int) prop;
                        if (numFastBytes < 5 || numFastBytes > Base.KMatchMaxLen)
                            throw new InvalidParamException();
                        this.numFastBytes = (uint) numFastBytes;
                        break;
                    }

                    case CoderPropId.Algorithm:
                    {
                        break;
                    }

                    case CoderPropId.MatchFinder:
                    {
                        if (!(prop is string))
                            throw new InvalidParamException();
                        var matchFinderIndexPrev = matchFinderType;
                        var m = FindMatchFinder(((string) prop).ToUpper());
                        if (m < 0)
                            throw new InvalidParamException();
                        matchFinderType = (EMatchFinderType) m;
                        if (matchFinder != null && matchFinderIndexPrev != matchFinderType)
                        {
                            dictionarySizePrev = 0xFFFFFFFF;
                            matchFinder = null;
                        }

                        break;
                    }

                    case CoderPropId.DictionarySize:
                    {
                        const int kDicLogSizeMaxCompress = 30;
                        if (!(prop is int))
                            throw new InvalidParamException();
                        ;
                        var dictionarySize = (int) prop;
                        if (dictionarySize < (uint) (1 << Base.KDicLogSizeMin) ||
                            dictionarySize > (uint) (1 << kDicLogSizeMaxCompress))
                            throw new InvalidParamException();
                        this.dictionarySize = (uint) dictionarySize;
                        int dicLogSize;
                        for (dicLogSize = 0; dicLogSize < (uint) kDicLogSizeMaxCompress; dicLogSize++)
                            if (dictionarySize <= (uint) 1 << dicLogSize)
                                break;
                        distTableSize = (uint) dicLogSize * 2;
                        break;
                    }

                    case CoderPropId.PosStateBits:
                    {
                        if (!(prop is int))
                            throw new InvalidParamException();
                        var v = (int) prop;
                        if (v < 0 || v > (uint) Base.KNumPosStatesBitsEncodingMax)
                            throw new InvalidParamException();
                        posStateBits = v;
                        posStateMask = ((uint) 1 << posStateBits) - 1;
                        break;
                    }

                    case CoderPropId.LitPosBits:
                    {
                        if (!(prop is int))
                            throw new InvalidParamException();
                        var v = (int) prop;
                        if (v < 0 || v > Base.KNumLitPosStatesBitsEncodingMax)
                            throw new InvalidParamException();
                        numLiteralPosStateBits = v;
                        break;
                    }

                    case CoderPropId.LitContextBits:
                    {
                        if (!(prop is int))
                            throw new InvalidParamException();
                        var v = (int) prop;
                        if (v < 0 || v > Base.KNumLitContextBitsMax)
                            throw new InvalidParamException();
                        ;
                        numLiteralContextBits = v;
                        break;
                    }

                    case CoderPropId.EndMarker:
                    {
                        if (!(prop is bool))
                            throw new InvalidParamException();
                        SetWriteEndMarkerMode((bool) prop);
                        break;
                    }

                    default:
                        throw new InvalidParamException();
                }
            }
        }

        public void WriteCoderProperties(Stream outStream)
        {
            properties[0] = (byte) ((posStateBits * 5 + numLiteralPosStateBits) * 9 + numLiteralContextBits);
            for (var i = 0; i < 4; i++)
                properties[1 + i] = (byte) ((dictionarySize >> (8 * i)) & 0xFF);
            outStream.Write(properties, 0, KPropSize);
        }

        private static uint GetPosSlot(uint pos)
        {
            if (pos < 1 << 11)
                return _gFastPos[pos];
            if (pos < 1 << 21)
                return (uint) (_gFastPos[pos >> 10] + 20);
            return (uint) (_gFastPos[pos >> 20] + 40);
        }

        private static uint GetPosSlot2(uint pos)
        {
            if (pos < 1 << 17)
                return (uint) (_gFastPos[pos >> 6] + 12);
            if (pos < 1 << 27)
                return (uint) (_gFastPos[pos >> 16] + 32);
            return (uint) (_gFastPos[pos >> 26] + 52);
        }

        private void BaseInit()
        {
            state.Init();
            previousByte = 0;
            for (uint i = 0; i < Base.KNumRepDistances; i++)
                repDistances[i] = 0;
        }

        private void Create()
        {
            if (matchFinder == null)
            {
                var bt = new BinTree();
                var numHashBytes = 4;
                if (matchFinderType == EMatchFinderType.Bt2)
                    numHashBytes = 2;
                bt.SetType(numHashBytes);
                matchFinder = bt;
            }

            literalEncoder.Create(numLiteralPosStateBits, numLiteralContextBits);

            if (dictionarySize == dictionarySizePrev && numFastBytesPrev == numFastBytes)
                return;
            matchFinder.Create(dictionarySize, KNumOpts, numFastBytes, Base.KMatchMaxLen + 1);
            dictionarySizePrev = dictionarySize;
            numFastBytesPrev = numFastBytes;
        }

        private void SetWriteEndMarkerMode(bool writeEndMarker)
        {
            writeEndMark = writeEndMarker;
        }

        private void Init()
        {
            BaseInit();
            rangeEncoder.Init();

            uint i;
            for (i = 0; i < Base.KNumStates; i++)
            {
                for (uint j = 0; j <= posStateMask; j++)
                {
                    var complexState = (i << Base.KNumPosStatesBitsMax) + j;
                    isMatch[complexState].Init();
                    isRep0Long[complexState].Init();
                }

                isRep[i].Init();
                isRepG0[i].Init();
                isRepG1[i].Init();
                isRepG2[i].Init();
            }

            literalEncoder.Init();
            for (i = 0; i < Base.KNumLenToPosStates; i++)
                posSlotEncoder[i].Init();
            for (i = 0; i < Base.KNumFullDistances - Base.KEndPosModelIndex; i++)
                posEncoders[i].Init();

            lenEncoder.Init((uint) 1 << posStateBits);
            repMatchLenEncoder.Init((uint) 1 << posStateBits);

            posAlignEncoder.Init();

            longestMatchWasFound = false;
            optimumEndIndex = 0;
            optimumCurrentIndex = 0;
            additionalOffset = 0;
        }

        private void ReadMatchDistances(out uint lenRes, out uint numDistancePairs)
        {
            lenRes = 0;
            numDistancePairs = matchFinder.GetMatches(matchDistances);
            if (numDistancePairs > 0)
            {
                lenRes = matchDistances[numDistancePairs - 2];
                if (lenRes == numFastBytes)
                    lenRes += matchFinder.GetMatchLen((int) lenRes - 1, matchDistances[numDistancePairs - 1],
                        Base.KMatchMaxLen - lenRes);
            }

            additionalOffset++;
        }


        private void MovePos(uint num)
        {
            if (num > 0)
            {
                matchFinder.Skip(num);
                additionalOffset += num;
            }
        }

        private uint GetRepLen1Price(Base.State state, uint posState)
        {
            return isRepG0[state.Index].GetPrice0() +
                   isRep0Long[(state.Index << Base.KNumPosStatesBitsMax) + posState].GetPrice0();
        }

        private uint GetPureRepPrice(uint repIndex, Base.State state, uint posState)
        {
            uint price;
            if (repIndex == 0)
            {
                price = isRepG0[state.Index].GetPrice0();
                price += isRep0Long[(state.Index << Base.KNumPosStatesBitsMax) + posState].GetPrice1();
            }
            else
            {
                price = isRepG0[state.Index].GetPrice1();
                if (repIndex == 1)
                {
                    price += isRepG1[state.Index].GetPrice0();
                }
                else
                {
                    price += isRepG1[state.Index].GetPrice1();
                    price += isRepG2[state.Index].GetPrice(repIndex - 2);
                }
            }

            return price;
        }

        private uint GetRepPrice(uint repIndex, uint len, Base.State state, uint posState)
        {
            var price = repMatchLenEncoder.GetPrice(len - Base.KMatchMinLen, posState);
            return price + GetPureRepPrice(repIndex, state, posState);
        }

        private uint GetPosLenPrice(uint pos, uint len, uint posState)
        {
            uint price;
            var lenToPosState = Base.GetLenToPosState(len);
            if (pos < Base.KNumFullDistances)
                price = distancesPrices[lenToPosState * Base.KNumFullDistances + pos];
            else
                price = posSlotPrices[(lenToPosState << Base.KNumPosSlotBits) + GetPosSlot2(pos)] +
                        alignPrices[pos & Base.KAlignMask];
            return price + lenEncoder.GetPrice(len - Base.KMatchMinLen, posState);
        }

        private uint Backward(out uint backRes, uint cur)
        {
            optimumEndIndex = cur;
            var posMem = optimum[cur].PosPrev;
            var backMem = optimum[cur].BackPrev;
            do
            {
                if (optimum[cur].Prev1IsChar)
                {
                    optimum[posMem].MakeAsChar();
                    optimum[posMem].PosPrev = posMem - 1;
                    if (optimum[cur].Prev2)
                    {
                        optimum[posMem - 1].Prev1IsChar = false;
                        optimum[posMem - 1].PosPrev = optimum[cur].PosPrev2;
                        optimum[posMem - 1].BackPrev = optimum[cur].BackPrev2;
                    }
                }

                var posPrev = posMem;
                var backCur = backMem;

                backMem = optimum[posPrev].BackPrev;
                posMem = optimum[posPrev].PosPrev;

                optimum[posPrev].BackPrev = backCur;
                optimum[posPrev].PosPrev = cur;
                cur = posPrev;
            } while (cur > 0);

            backRes = optimum[0].BackPrev;
            optimumCurrentIndex = optimum[0].PosPrev;
            return optimumCurrentIndex;
        }


        private uint GetOptimum(uint position, out uint backRes)
        {
            if (optimumEndIndex != optimumCurrentIndex)
            {
                var lenRes = optimum[optimumCurrentIndex].PosPrev - optimumCurrentIndex;
                backRes = optimum[optimumCurrentIndex].BackPrev;
                optimumCurrentIndex = optimum[optimumCurrentIndex].PosPrev;
                return lenRes;
            }

            optimumCurrentIndex = optimumEndIndex = 0;

            uint lenMain, numDistancePairs;
            if (!longestMatchWasFound)
            {
                ReadMatchDistances(out lenMain, out numDistancePairs);
            }
            else
            {
                lenMain = longestMatchLength;
                numDistancePairs = this.numDistancePairs;
                longestMatchWasFound = false;
            }

            var numAvailableBytes = matchFinder.GetNumAvailableBytes() + 1;
            if (numAvailableBytes < 2)
            {
                backRes = 0xFFFFFFFF;
                return 1;
            }

            if (numAvailableBytes > Base.KMatchMaxLen)
                numAvailableBytes = Base.KMatchMaxLen;

            uint repMaxIndex = 0;
            uint i;
            for (i = 0; i < Base.KNumRepDistances; i++)
            {
                reps[i] = repDistances[i];
                repLens[i] = matchFinder.GetMatchLen(0 - 1, reps[i], Base.KMatchMaxLen);
                if (repLens[i] > repLens[repMaxIndex])
                    repMaxIndex = i;
            }

            if (repLens[repMaxIndex] >= numFastBytes)
            {
                backRes = repMaxIndex;
                var lenRes = repLens[repMaxIndex];
                MovePos(lenRes - 1);
                return lenRes;
            }

            if (lenMain >= numFastBytes)
            {
                backRes = matchDistances[numDistancePairs - 1] + Base.KNumRepDistances;
                MovePos(lenMain - 1);
                return lenMain;
            }

            var currentByte = matchFinder.GetIndexByte(0 - 1);
            var matchByte = matchFinder.GetIndexByte((int) (0 - repDistances[0] - 1 - 1));

            if (lenMain < 2 && currentByte != matchByte && repLens[repMaxIndex] < 2)
            {
                backRes = 0xFFFFFFFF;
                return 1;
            }

            optimum[0].State = state;

            var posState = position & posStateMask;

            optimum[1].Price = isMatch[(state.Index << Base.KNumPosStatesBitsMax) + posState].GetPrice0() +
                               literalEncoder.GetSubCoder(position, previousByte)
                                   .GetPrice(!state.IsCharState(), matchByte, currentByte);
            optimum[1].MakeAsChar();

            var matchPrice = isMatch[(state.Index << Base.KNumPosStatesBitsMax) + posState].GetPrice1();
            var repMatchPrice = matchPrice + isRep[state.Index].GetPrice1();

            if (matchByte == currentByte)
            {
                var shortRepPrice = repMatchPrice + GetRepLen1Price(state, posState);
                if (shortRepPrice < optimum[1].Price)
                {
                    optimum[1].Price = shortRepPrice;
                    optimum[1].MakeAsShortRep();
                }
            }

            var lenEnd = lenMain >= repLens[repMaxIndex] ? lenMain : repLens[repMaxIndex];

            if (lenEnd < 2)
            {
                backRes = optimum[1].BackPrev;
                return 1;
            }

            optimum[1].PosPrev = 0;

            optimum[0].Backs0 = reps[0];
            optimum[0].Backs1 = reps[1];
            optimum[0].Backs2 = reps[2];
            optimum[0].Backs3 = reps[3];

            var len = lenEnd;
            do
            {
                optimum[len--].Price = KInfinityPrice;
            } while (len >= 2);

            for (i = 0; i < Base.KNumRepDistances; i++)
            {
                var repLen = repLens[i];
                if (repLen < 2)
                    continue;
                var price = repMatchPrice + GetPureRepPrice(i, state, posState);
                do
                {
                    var curAndLenPrice = price + repMatchLenEncoder.GetPrice(repLen - 2, posState);
                    var optimum = this.optimum[repLen];
                    if (curAndLenPrice < optimum.Price)
                    {
                        optimum.Price = curAndLenPrice;
                        optimum.PosPrev = 0;
                        optimum.BackPrev = i;
                        optimum.Prev1IsChar = false;
                    }
                } while (--repLen >= 2);
            }

            var normalMatchPrice = matchPrice + isRep[state.Index].GetPrice0();

            len = repLens[0] >= 2 ? repLens[0] + 1 : 2;
            if (len <= lenMain)
            {
                uint offs = 0;
                while (len > matchDistances[offs])
                    offs += 2;
                for (;; len++)
                {
                    var distance = matchDistances[offs + 1];
                    var curAndLenPrice = normalMatchPrice + GetPosLenPrice(distance, len, posState);
                    var optimum = this.optimum[len];
                    if (curAndLenPrice < optimum.Price)
                    {
                        optimum.Price = curAndLenPrice;
                        optimum.PosPrev = 0;
                        optimum.BackPrev = distance + Base.KNumRepDistances;
                        optimum.Prev1IsChar = false;
                    }

                    if (len == matchDistances[offs])
                    {
                        offs += 2;
                        if (offs == numDistancePairs)
                            break;
                    }
                }
            }

            uint cur = 0;

            while (true)
            {
                cur++;
                if (cur == lenEnd)
                    return Backward(out backRes, cur);
                uint newLen;
                ReadMatchDistances(out newLen, out numDistancePairs);
                if (newLen >= numFastBytes)
                {
                    this.numDistancePairs = numDistancePairs;
                    longestMatchLength = newLen;
                    longestMatchWasFound = true;
                    return Backward(out backRes, cur);
                }

                position++;
                var posPrev = optimum[cur].PosPrev;
                Base.State state;
                if (optimum[cur].Prev1IsChar)
                {
                    posPrev--;
                    if (optimum[cur].Prev2)
                    {
                        state = optimum[optimum[cur].PosPrev2].State;
                        if (optimum[cur].BackPrev2 < Base.KNumRepDistances)
                            state.UpdateRep();
                        else
                            state.UpdateMatch();
                    }
                    else
                    {
                        state = optimum[posPrev].State;
                    }

                    state.UpdateChar();
                }
                else
                {
                    state = optimum[posPrev].State;
                }

                if (posPrev == cur - 1)
                {
                    if (optimum[cur].IsShortRep())
                        state.UpdateShortRep();
                    else
                        state.UpdateChar();
                }
                else
                {
                    uint pos;
                    if (optimum[cur].Prev1IsChar && optimum[cur].Prev2)
                    {
                        posPrev = optimum[cur].PosPrev2;
                        pos = optimum[cur].BackPrev2;
                        state.UpdateRep();
                    }
                    else
                    {
                        pos = optimum[cur].BackPrev;
                        if (pos < Base.KNumRepDistances)
                            state.UpdateRep();
                        else
                            state.UpdateMatch();
                    }

                    var opt = optimum[posPrev];
                    if (pos < Base.KNumRepDistances)
                    {
                        if (pos == 0)
                        {
                            reps[0] = opt.Backs0;
                            reps[1] = opt.Backs1;
                            reps[2] = opt.Backs2;
                            reps[3] = opt.Backs3;
                        }
                        else if (pos == 1)
                        {
                            reps[0] = opt.Backs1;
                            reps[1] = opt.Backs0;
                            reps[2] = opt.Backs2;
                            reps[3] = opt.Backs3;
                        }
                        else if (pos == 2)
                        {
                            reps[0] = opt.Backs2;
                            reps[1] = opt.Backs0;
                            reps[2] = opt.Backs1;
                            reps[3] = opt.Backs3;
                        }
                        else
                        {
                            reps[0] = opt.Backs3;
                            reps[1] = opt.Backs0;
                            reps[2] = opt.Backs1;
                            reps[3] = opt.Backs2;
                        }
                    }
                    else
                    {
                        reps[0] = pos - Base.KNumRepDistances;
                        reps[1] = opt.Backs0;
                        reps[2] = opt.Backs1;
                        reps[3] = opt.Backs2;
                    }
                }

                optimum[cur].State = state;
                optimum[cur].Backs0 = reps[0];
                optimum[cur].Backs1 = reps[1];
                optimum[cur].Backs2 = reps[2];
                optimum[cur].Backs3 = reps[3];
                var curPrice = optimum[cur].Price;

                currentByte = matchFinder.GetIndexByte(0 - 1);
                matchByte = matchFinder.GetIndexByte((int) (0 - reps[0] - 1 - 1));

                posState = position & posStateMask;

                var curAnd1Price = curPrice +
                                   isMatch[(state.Index << Base.KNumPosStatesBitsMax) + posState].GetPrice0() +
                                   literalEncoder.GetSubCoder(position, matchFinder.GetIndexByte(0 - 2))
                                       .GetPrice(!state.IsCharState(), matchByte, currentByte);

                var nextOptimum = optimum[cur + 1];

                var nextIsChar = false;
                if (curAnd1Price < nextOptimum.Price)
                {
                    nextOptimum.Price = curAnd1Price;
                    nextOptimum.PosPrev = cur;
                    nextOptimum.MakeAsChar();
                    nextIsChar = true;
                }

                matchPrice = curPrice + isMatch[(state.Index << Base.KNumPosStatesBitsMax) + posState].GetPrice1();
                repMatchPrice = matchPrice + isRep[state.Index].GetPrice1();

                if (matchByte == currentByte &&
                    !(nextOptimum.PosPrev < cur && nextOptimum.BackPrev == 0))
                {
                    var shortRepPrice = repMatchPrice + GetRepLen1Price(state, posState);
                    if (shortRepPrice <= nextOptimum.Price)
                    {
                        nextOptimum.Price = shortRepPrice;
                        nextOptimum.PosPrev = cur;
                        nextOptimum.MakeAsShortRep();
                        nextIsChar = true;
                    }
                }

                var numAvailableBytesFull = matchFinder.GetNumAvailableBytes() + 1;
                numAvailableBytesFull = Math.Min(KNumOpts - 1 - cur, numAvailableBytesFull);
                numAvailableBytes = numAvailableBytesFull;

                if (numAvailableBytes < 2)
                    continue;
                if (numAvailableBytes > numFastBytes)
                    numAvailableBytes = numFastBytes;
                if (!nextIsChar && matchByte != currentByte)
                {
                    // try Literal + rep0
                    var t = Math.Min(numAvailableBytesFull - 1, numFastBytes);
                    var lenTest2 = matchFinder.GetMatchLen(0, reps[0], t);
                    if (lenTest2 >= 2)
                    {
                        var state2 = state;
                        state2.UpdateChar();
                        var posStateNext = (position + 1) & posStateMask;
                        var nextRepMatchPrice = curAnd1Price +
                                                isMatch[(state2.Index << Base.KNumPosStatesBitsMax) + posStateNext]
                                                    .GetPrice1() +
                                                isRep[state2.Index].GetPrice1();
                        {
                            var offset = cur + 1 + lenTest2;
                            while (lenEnd < offset)
                                this.optimum[++lenEnd].Price = KInfinityPrice;
                            var curAndLenPrice = nextRepMatchPrice + GetRepPrice(
                                                     0, lenTest2, state2, posStateNext);
                            var optimum = this.optimum[offset];
                            if (curAndLenPrice < optimum.Price)
                            {
                                optimum.Price = curAndLenPrice;
                                optimum.PosPrev = cur + 1;
                                optimum.BackPrev = 0;
                                optimum.Prev1IsChar = true;
                                optimum.Prev2 = false;
                            }
                        }
                    }
                }

                uint startLen = 2; // speed optimization 

                for (uint repIndex = 0; repIndex < Base.KNumRepDistances; repIndex++)
                {
                    var lenTest = matchFinder.GetMatchLen(0 - 1, reps[repIndex], numAvailableBytes);
                    if (lenTest < 2)
                        continue;
                    var lenTestTemp = lenTest;
                    do
                    {
                        while (lenEnd < cur + lenTest)
                            this.optimum[++lenEnd].Price = KInfinityPrice;
                        var curAndLenPrice = repMatchPrice + GetRepPrice(repIndex, lenTest, state, posState);
                        var optimum = this.optimum[cur + lenTest];
                        if (curAndLenPrice < optimum.Price)
                        {
                            optimum.Price = curAndLenPrice;
                            optimum.PosPrev = cur;
                            optimum.BackPrev = repIndex;
                            optimum.Prev1IsChar = false;
                        }
                    } while (--lenTest >= 2);

                    lenTest = lenTestTemp;

                    if (repIndex == 0)
                        startLen = lenTest + 1;

                    // if (_maxMode)
                    if (lenTest >= numAvailableBytesFull)
                        continue;
                    {
                        var t = Math.Min(numAvailableBytesFull - 1 - lenTest, numFastBytes);
                        var lenTest2 = matchFinder.GetMatchLen((int) lenTest, reps[repIndex], t);
                        if (lenTest2 < 2)
                            continue;
                        var state2 = state;
                        state2.UpdateRep();
                        var posStateNext = (position + lenTest) & posStateMask;
                        var curAndLenCharPrice =
                            repMatchPrice + GetRepPrice(repIndex, lenTest, state, posState) +
                            isMatch[(state2.Index << Base.KNumPosStatesBitsMax) + posStateNext].GetPrice0() +
                            literalEncoder.GetSubCoder(position + lenTest,
                                matchFinder.GetIndexByte((int) lenTest - 1 - 1)).GetPrice(true,
                                matchFinder.GetIndexByte((int) lenTest - 1 - (int) (reps[repIndex] + 1)),
                                matchFinder.GetIndexByte((int) lenTest - 1));
                        state2.UpdateChar();
                        posStateNext = (position + lenTest + 1) & posStateMask;
                        var nextMatchPrice = curAndLenCharPrice +
                                             isMatch[(state2.Index << Base.KNumPosStatesBitsMax) + posStateNext]
                                                 .GetPrice1();
                        var nextRepMatchPrice = nextMatchPrice + isRep[state2.Index].GetPrice1();

                        // for(; lenTest2 >= 2; lenTest2--)
                        {
                            var offset = lenTest + 1 + lenTest2;
                            while (lenEnd < cur + offset)
                                this.optimum[++lenEnd].Price = KInfinityPrice;
                            var curAndLenPrice = nextRepMatchPrice + GetRepPrice(0, lenTest2, state2, posStateNext);
                            var optimum = this.optimum[cur + offset];
                            if (curAndLenPrice >= optimum.Price)
                                continue;
                            optimum.Price = curAndLenPrice;
                            optimum.PosPrev = cur + lenTest + 1;
                            optimum.BackPrev = 0;
                            optimum.Prev1IsChar = true;
                            optimum.Prev2 = true;
                            optimum.PosPrev2 = cur;
                            optimum.BackPrev2 = repIndex;
                        }
                    }
                }

                if (newLen > numAvailableBytes)
                {
                    newLen = numAvailableBytes;
                    for (numDistancePairs = 0; newLen > matchDistances[numDistancePairs]; numDistancePairs += 2) ;
                    matchDistances[numDistancePairs] = newLen;
                    numDistancePairs += 2;
                }

                if (newLen < startLen)
                    continue;
                {
                    normalMatchPrice = matchPrice + isRep[state.Index].GetPrice0();
                    while (lenEnd < cur + newLen)
                        optimum[++lenEnd].Price = KInfinityPrice;

                    uint offs = 0;
                    while (startLen > matchDistances[offs])
                        offs += 2;

                    for (var lenTest = startLen;; lenTest++)
                    {
                        var curBack = matchDistances[offs + 1];
                        var curAndLenPrice = normalMatchPrice + GetPosLenPrice(curBack, lenTest, posState);
                        var optimum = this.optimum[cur + lenTest];
                        if (curAndLenPrice < optimum.Price)
                        {
                            optimum.Price = curAndLenPrice;
                            optimum.PosPrev = cur;
                            optimum.BackPrev = curBack + Base.KNumRepDistances;
                            optimum.Prev1IsChar = false;
                        }

                        if (lenTest != matchDistances[offs])
                            continue;
                        if (lenTest < numAvailableBytesFull)
                        {
                            var t = Math.Min(numAvailableBytesFull - 1 - lenTest, numFastBytes);
                            var lenTest2 = matchFinder.GetMatchLen((int) lenTest, curBack, t);
                            if (lenTest2 >= 2)
                            {
                                var state2 = state;
                                state2.UpdateMatch();
                                var posStateNext = (position + lenTest) & posStateMask;
                                var curAndLenCharPrice = curAndLenPrice +
                                                         isMatch[
                                                             (state2.Index << Base.KNumPosStatesBitsMax) +
                                                             posStateNext].GetPrice0() +
                                                         literalEncoder.GetSubCoder(position + lenTest,
                                                                 matchFinder.GetIndexByte((int) lenTest - 1 - 1))
                                                             .GetPrice(true,
                                                                 matchFinder.GetIndexByte(
                                                                     (int) lenTest - (int) (curBack + 1) - 1),
                                                                 matchFinder.GetIndexByte((int) lenTest - 1));
                                state2.UpdateChar();
                                posStateNext = (position + lenTest + 1) & posStateMask;
                                var nextMatchPrice =
                                    curAndLenCharPrice +
                                    isMatch[(state2.Index << Base.KNumPosStatesBitsMax) + posStateNext].GetPrice1();
                                var nextRepMatchPrice = nextMatchPrice + isRep[state2.Index].GetPrice1();

                                var offset = lenTest + 1 + lenTest2;
                                while (lenEnd < cur + offset)
                                    this.optimum[++lenEnd].Price = KInfinityPrice;
                                curAndLenPrice = nextRepMatchPrice + GetRepPrice(0, lenTest2, state2, posStateNext);
                                optimum = this.optimum[cur + offset];
                                if (curAndLenPrice < optimum.Price)
                                {
                                    optimum.Price = curAndLenPrice;
                                    optimum.PosPrev = cur + lenTest + 1;
                                    optimum.BackPrev = 0;
                                    optimum.Prev1IsChar = true;
                                    optimum.Prev2 = true;
                                    optimum.PosPrev2 = cur;
                                    optimum.BackPrev2 = curBack + Base.KNumRepDistances;
                                }
                            }
                        }

                        offs += 2;
                        if (offs == numDistancePairs)
                            break;
                    }
                }
            }
        }

        private bool ChangePair(uint smallDist, uint bigDist)
        {
            const int kDif = 7;
            return smallDist < (uint) 1 << (32 - kDif) && bigDist >= smallDist << kDif;
        }

        private void WriteEndMarker(uint posState)
        {
            if (!writeEndMark)
                return;

            isMatch[(state.Index << Base.KNumPosStatesBitsMax) + posState].Encode(rangeEncoder, 1);
            isRep[state.Index].Encode(rangeEncoder, 0);
            state.UpdateMatch();
            var len = Base.KMatchMinLen;
            lenEncoder.Encode(rangeEncoder, len - Base.KMatchMinLen, posState);
            uint posSlot = (1 << Base.KNumPosSlotBits) - 1;
            var lenToPosState = Base.GetLenToPosState(len);
            posSlotEncoder[lenToPosState].Encode(rangeEncoder, posSlot);
            var footerBits = 30;
            var posReduced = ((uint) 1 << footerBits) - 1;
            rangeEncoder.EncodeDirectBits(posReduced >> Base.KNumAlignBits, footerBits - Base.KNumAlignBits);
            posAlignEncoder.ReverseEncode(rangeEncoder, posReduced & Base.KAlignMask);
        }

        private void Flush(uint nowPos)
        {
            ReleaseMfStream();
            WriteEndMarker(nowPos & posStateMask);
            rangeEncoder.FlushData();
            rangeEncoder.FlushStream();
        }

        public void CodeOneBlock(out long inSize, out long outSize, out bool finished)
        {
            inSize = 0;
            outSize = 0;
            finished = true;

            if (inStream != null)
            {
                matchFinder.SetStream(inStream);
                matchFinder.Init();
                needReleaseMfStream = true;
                inStream = null;
                if (trainSize > 0)
                    matchFinder.Skip(trainSize);
            }

            if (this.finished)
                return;
            this.finished = true;


            var progressPosValuePrev = nowPos64;
            if (nowPos64 == 0)
            {
                if (matchFinder.GetNumAvailableBytes() == 0)
                {
                    Flush((uint) nowPos64);
                    return;
                }

                uint len, numDistancePairs; // it's not used
                ReadMatchDistances(out len, out numDistancePairs);
                var posState = (uint) nowPos64 & posStateMask;
                isMatch[(state.Index << Base.KNumPosStatesBitsMax) + posState].Encode(rangeEncoder, 0);
                state.UpdateChar();
                var curByte = matchFinder.GetIndexByte((int) (0 - additionalOffset));
                literalEncoder.GetSubCoder((uint) nowPos64, previousByte).Encode(rangeEncoder, curByte);
                previousByte = curByte;
                additionalOffset--;
                nowPos64++;
            }

            if (matchFinder.GetNumAvailableBytes() == 0)
            {
                Flush((uint) nowPos64);
                return;
            }

            while (true)
            {
                uint pos;
                var len = GetOptimum((uint) nowPos64, out pos);

                var posState = (uint) nowPos64 & posStateMask;
                var complexState = (state.Index << Base.KNumPosStatesBitsMax) + posState;
                if (len == 1 && pos == 0xFFFFFFFF)
                {
                    isMatch[complexState].Encode(rangeEncoder, 0);
                    var curByte = matchFinder.GetIndexByte((int) (0 - additionalOffset));
                    var subCoder = literalEncoder.GetSubCoder((uint) nowPos64, previousByte);
                    if (!state.IsCharState())
                    {
                        var matchByte = matchFinder.GetIndexByte((int) (0 - repDistances[0] - 1 - additionalOffset));
                        subCoder.EncodeMatched(rangeEncoder, matchByte, curByte);
                    }
                    else
                    {
                        subCoder.Encode(rangeEncoder, curByte);
                    }

                    previousByte = curByte;
                    state.UpdateChar();
                }
                else
                {
                    isMatch[complexState].Encode(rangeEncoder, 1);
                    if (pos < Base.KNumRepDistances)
                    {
                        isRep[state.Index].Encode(rangeEncoder, 1);
                        if (pos == 0)
                        {
                            isRepG0[state.Index].Encode(rangeEncoder, 0);
                            isRep0Long[complexState].Encode(rangeEncoder, len == 1 ? 0 : (uint) 1);
                        }
                        else
                        {
                            isRepG0[state.Index].Encode(rangeEncoder, 1);
                            if (pos == 1)
                            {
                                isRepG1[state.Index].Encode(rangeEncoder, 0);
                            }
                            else
                            {
                                isRepG1[state.Index].Encode(rangeEncoder, 1);
                                isRepG2[state.Index].Encode(rangeEncoder, pos - 2);
                            }
                        }

                        if (len == 1)
                        {
                            state.UpdateShortRep();
                        }
                        else
                        {
                            repMatchLenEncoder.Encode(rangeEncoder, len - Base.KMatchMinLen, posState);
                            state.UpdateRep();
                        }

                        var distance = repDistances[pos];
                        if (pos != 0)
                        {
                            for (var i = pos; i >= 1; i--)
                                repDistances[i] = repDistances[i - 1];
                            repDistances[0] = distance;
                        }
                    }
                    else
                    {
                        isRep[state.Index].Encode(rangeEncoder, 0);
                        state.UpdateMatch();
                        lenEncoder.Encode(rangeEncoder, len - Base.KMatchMinLen, posState);
                        pos -= Base.KNumRepDistances;
                        var posSlot = GetPosSlot(pos);
                        var lenToPosState = Base.GetLenToPosState(len);
                        posSlotEncoder[lenToPosState].Encode(rangeEncoder, posSlot);

                        if (posSlot >= Base.KStartPosModelIndex)
                        {
                            var footerBits = (int) ((posSlot >> 1) - 1);
                            var baseVal = (2 | (posSlot & 1)) << footerBits;
                            var posReduced = pos - baseVal;

                            if (posSlot < Base.KEndPosModelIndex)
                            {
                                BitTreeEncoder.ReverseEncode(posEncoders,
                                    baseVal - posSlot - 1, rangeEncoder, footerBits, posReduced);
                            }
                            else
                            {
                                rangeEncoder.EncodeDirectBits(posReduced >> Base.KNumAlignBits,
                                    footerBits - Base.KNumAlignBits);
                                posAlignEncoder.ReverseEncode(rangeEncoder, posReduced & Base.KAlignMask);
                                alignPriceCount++;
                            }
                        }

                        var distance = pos;
                        for (var i = Base.KNumRepDistances - 1; i >= 1; i--)
                            repDistances[i] = repDistances[i - 1];
                        repDistances[0] = distance;
                        matchPriceCount++;
                    }

                    previousByte = matchFinder.GetIndexByte((int) (len - 1 - additionalOffset));
                }

                additionalOffset -= len;
                nowPos64 += len;
                if (additionalOffset == 0)
                {
                    // if (!_fastMode)
                    if (matchPriceCount >= 1 << 7)
                        FillDistancesPrices();
                    if (alignPriceCount >= Base.KAlignTableSize)
                        FillAlignPrices();
                    inSize = nowPos64;
                    outSize = rangeEncoder.GetProcessedSizeAdd();
                    if (matchFinder.GetNumAvailableBytes() == 0)
                    {
                        Flush((uint) nowPos64);
                        return;
                    }

                    if (nowPos64 - progressPosValuePrev < 1 << 12)
                        continue;
                    this.finished = false;
                    finished = false;
                    return;
                }
            }
        }

        private void ReleaseMfStream()
        {
            if (matchFinder == null || !needReleaseMfStream)
                return;
            matchFinder.ReleaseStream();
            needReleaseMfStream = false;
        }

        private void SetOutStream(Stream outStream)
        {
            rangeEncoder.SetStream(outStream);
        }

        private void ReleaseOutStream()
        {
            rangeEncoder.ReleaseStream();
        }

        private void ReleaseStreams()
        {
            ReleaseMfStream();
            ReleaseOutStream();
        }

        private void SetStreams(Stream inStream, Stream outStream,
            long inSize, long outSize)
        {
            this.inStream = inStream;
            finished = false;
            Create();
            SetOutStream(outStream);
            Init();
            FillDistancesPrices();
            FillAlignPrices();
            
            lenEncoder.SetTableSize(numFastBytes + 1 - Base.KMatchMinLen);
            lenEncoder.UpdateTables((uint) 1 << posStateBits);
            repMatchLenEncoder.SetTableSize(numFastBytes + 1 - Base.KMatchMinLen);
            repMatchLenEncoder.UpdateTables((uint) 1 << posStateBits);

            nowPos64 = 0;
        }

        private void FillDistancesPrices()
        {
            for (var i = Base.KStartPosModelIndex; i < Base.KNumFullDistances; i++)
            {
                var posSlot = GetPosSlot(i);
                var footerBits = (int) ((posSlot >> 1) - 1);
                var baseVal = (2 | (posSlot & 1)) << footerBits;
                tempPrices[i] = BitTreeEncoder.ReverseGetPrice(posEncoders,
                    baseVal - posSlot - 1, footerBits, i - baseVal);
            }

            for (uint lenToPosState = 0; lenToPosState < Base.KNumLenToPosStates; lenToPosState++)
            {
                uint posSlot;
                var encoder = posSlotEncoder[lenToPosState];

                var st = lenToPosState << Base.KNumPosSlotBits;
                for (posSlot = 0; posSlot < distTableSize; posSlot++)
                    posSlotPrices[st + posSlot] = encoder.GetPrice(posSlot);
                for (posSlot = Base.KEndPosModelIndex; posSlot < distTableSize; posSlot++)
                    posSlotPrices[st + posSlot] +=
                        ((posSlot >> 1) - 1 - Base.KNumAlignBits) << BitEncoder.KNumBitPriceShiftBits;

                var st2 = lenToPosState * Base.KNumFullDistances;
                uint i;
                for (i = 0; i < Base.KStartPosModelIndex; i++)
                    distancesPrices[st2 + i] = posSlotPrices[st + i];
                for (; i < Base.KNumFullDistances; i++)
                    distancesPrices[st2 + i] = posSlotPrices[st + GetPosSlot(i)] + tempPrices[i];
            }

            matchPriceCount = 0;
        }

        private void FillAlignPrices()
        {
            for (uint i = 0; i < Base.KAlignTableSize; i++)
                alignPrices[i] = posAlignEncoder.ReverseGetPrice(i);
            alignPriceCount = 0;
        }

        private static int FindMatchFinder(string s)
        {
            for (var m = 0; m < _kMatchFinderIDs.Length; m++)
                if (s == _kMatchFinderIDs[m])
                    return m;
            return -1;
        }

        public void SetTrainSize(uint trainSize)
        {
            this.trainSize = trainSize;
        }

        private enum EMatchFinderType
        {
            Bt2,
            Bt4
        }

        private class LiteralEncoder
        {
            private Encoder2[] mCoders;
            private int mNumPosBits;
            private int mNumPrevBits;
            private uint mPosMask;

            public void Create(int numPosBits, int numPrevBits)
            {
                if (mCoders != null && mNumPrevBits == numPrevBits && mNumPosBits == numPosBits)
                    return;
                mNumPosBits = numPosBits;
                mPosMask = ((uint) 1 << numPosBits) - 1;
                mNumPrevBits = numPrevBits;
                var numStates = (uint) 1 << (mNumPrevBits + mNumPosBits);
                mCoders = new Encoder2[numStates];
                for (uint i = 0; i < numStates; i++)
                    mCoders[i].Create();
            }

            public void Init()
            {
                var numStates = (uint) 1 << (mNumPrevBits + mNumPosBits);
                for (uint i = 0; i < numStates; i++)
                    mCoders[i].Init();
            }

            public Encoder2 GetSubCoder(uint pos, byte prevByte)
            {
                return mCoders[((pos & mPosMask) << mNumPrevBits) + (uint) (prevByte >> (8 - mNumPrevBits))];
            }

            public struct Encoder2
            {
                private BitEncoder[] mEncoders;

                public void Create()
                {
                    mEncoders = new BitEncoder[0x300];
                }

                public void Init()
                {
                    for (var i = 0; i < 0x300; i++) mEncoders[i].Init();
                }

                public void Encode(RangeCoder.Encoder rangeEncoder, byte symbol)
                {
                    uint context = 1;
                    for (var i = 7; i >= 0; i--)
                    {
                        var bit = (uint) ((symbol >> i) & 1);
                        mEncoders[context].Encode(rangeEncoder, bit);
                        context = (context << 1) | bit;
                    }
                }

                public void EncodeMatched(RangeCoder.Encoder rangeEncoder, byte matchByte, byte symbol)
                {
                    uint context = 1;
                    var same = true;
                    for (var i = 7; i >= 0; i--)
                    {
                        var bit = (uint) ((symbol >> i) & 1);
                        var state = context;
                        if (same)
                        {
                            var matchBit = (uint) ((matchByte >> i) & 1);
                            state += (1 + matchBit) << 8;
                            same = matchBit == bit;
                        }

                        mEncoders[state].Encode(rangeEncoder, bit);
                        context = (context << 1) | bit;
                    }
                }

                public uint GetPrice(bool matchMode, byte matchByte, byte symbol)
                {
                    uint price = 0;
                    uint context = 1;
                    var i = 7;
                    if (matchMode)
                        for (; i >= 0; i--)
                        {
                            var matchBit = (uint) (matchByte >> i) & 1;
                            var bit = (uint) (symbol >> i) & 1;
                            price += mEncoders[((1 + matchBit) << 8) + context].GetPrice(bit);
                            context = (context << 1) | bit;
                            if (matchBit != bit)
                            {
                                i--;
                                break;
                            }
                        }

                    for (; i >= 0; i--)
                    {
                        var bit = (uint) (symbol >> i) & 1;
                        price += mEncoders[context].GetPrice(bit);
                        context = (context << 1) | bit;
                    }

                    return price;
                }
            }
        }

        private class LenEncoder
        {
            private BitEncoder choice = new BitEncoder();
            private BitEncoder choice2 = new BitEncoder();
            private BitTreeEncoder highCoder = new BitTreeEncoder(Base.KNumHighLenBits);
            private readonly BitTreeEncoder[] lowCoder = new BitTreeEncoder[Base.KNumPosStatesEncodingMax];
            private readonly BitTreeEncoder[] midCoder = new BitTreeEncoder[Base.KNumPosStatesEncodingMax];

            protected LenEncoder()
            {
                for (uint posState = 0; posState < Base.KNumPosStatesEncodingMax; posState++)
                {
                    lowCoder[posState] = new BitTreeEncoder(Base.KNumLowLenBits);
                    midCoder[posState] = new BitTreeEncoder(Base.KNumMidLenBits);
                }
            }

            public void Init(uint numPosStates)
            {
                choice.Init();
                choice2.Init();
                for (uint posState = 0; posState < numPosStates; posState++)
                {
                    lowCoder[posState].Init();
                    midCoder[posState].Init();
                }

                highCoder.Init();
            }

            public void Encode(RangeCoder.Encoder rangeEncoder, uint symbol, uint posState)
            {
                if (symbol < Base.KNumLowLenSymbols)
                {
                    choice.Encode(rangeEncoder, 0);
                    lowCoder[posState].Encode(rangeEncoder, symbol);
                }
                else
                {
                    symbol -= Base.KNumLowLenSymbols;
                    choice.Encode(rangeEncoder, 1);
                    if (symbol < Base.KNumMidLenSymbols)
                    {
                        choice2.Encode(rangeEncoder, 0);
                        midCoder[posState].Encode(rangeEncoder, symbol);
                    }
                    else
                    {
                        choice2.Encode(rangeEncoder, 1);
                        highCoder.Encode(rangeEncoder, symbol - Base.KNumMidLenSymbols);
                    }
                }
            }

            public void SetPrices(uint posState, uint numSymbols, uint[] prices, uint st)
            {
                var a0 = choice.GetPrice0();
                var a1 = choice.GetPrice1();
                var b0 = a1 + choice2.GetPrice0();
                var b1 = a1 + choice2.GetPrice1();
                uint i = 0;
                for (i = 0; i < Base.KNumLowLenSymbols; i++)
                {
                    if (i >= numSymbols)
                        return;
                    prices[st + i] = a0 + lowCoder[posState].GetPrice(i);
                }

                for (; i < Base.KNumLowLenSymbols + Base.KNumMidLenSymbols; i++)
                {
                    if (i >= numSymbols)
                        return;
                    prices[st + i] = b0 + midCoder[posState].GetPrice(i - Base.KNumLowLenSymbols);
                }

                for (; i < numSymbols; i++)
                    prices[st + i] = b1 + highCoder.GetPrice(i - Base.KNumLowLenSymbols - Base.KNumMidLenSymbols);
            }
        }

        private class LenPriceTableEncoder : LenEncoder
        {
            private readonly uint[] counters = new uint[Base.KNumPosStatesEncodingMax];
            private readonly uint[] prices = new uint[Base.KNumLenSymbols << Base.KNumPosStatesBitsEncodingMax];
            private uint tableSize;

            public void SetTableSize(uint tableSize)
            {
                this.tableSize = tableSize;
            }

            public uint GetPrice(uint symbol, uint posState)
            {
                return prices[posState * Base.KNumLenSymbols + symbol];
            }

            private void UpdateTable(uint posState)
            {
                SetPrices(posState, tableSize, prices, posState * Base.KNumLenSymbols);
                counters[posState] = tableSize;
            }

            public void UpdateTables(uint numPosStates)
            {
                for (uint posState = 0; posState < numPosStates; posState++)
                    UpdateTable(posState);
            }

            public new void Encode(RangeCoder.Encoder rangeEncoder, uint symbol, uint posState)
            {
                base.Encode(rangeEncoder, symbol, posState);
                if (--counters[posState] == 0)
                    UpdateTable(posState);
            }
        }

        private class Optimal
        {
            public uint BackPrev;
            public uint BackPrev2;

            public uint Backs0;
            public uint Backs1;
            public uint Backs2;
            public uint Backs3;
            public uint PosPrev;

            public uint PosPrev2;

            public bool Prev1IsChar;
            public bool Prev2;

            public uint Price;
            public Base.State State;

            public void MakeAsChar()
            {
                BackPrev = 0xFFFFFFFF;
                Prev1IsChar = false;
            }

            public void MakeAsShortRep()
            {
                BackPrev = 0;
                ;
                Prev1IsChar = false;
            }

            public bool IsShortRep()
            {
                return BackPrev == 0;
            }
        }
    }
}