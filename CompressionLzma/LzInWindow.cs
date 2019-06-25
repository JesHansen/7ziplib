// LzInWindow.cs

using System.IO;

namespace SevenZip.Compression.LZ
{
    public class InWindow
    {
        public uint BlockSize; // Size of Allocated memory block
        public byte[] BufferBase; // pointer to buffer with data

        public uint BufferOffset;
        private uint keepSizeAfter; // how many BYTEs must be kept buffer after _pos
        private uint keepSizeBefore; // how many BYTEs must be kept in buffer before _pos

        private uint pointerToLastSafePosition;
        public uint Pos; // offset (from _buffer) of curent byte
        private uint posLimit; // offset (from _buffer) of first byte when new block reading must be done
        private Stream stream;
        private bool streamEndWasReached; // if (true) then _streamPos shows real end of stream
        public uint StreamPos; // offset (from _buffer) of first not read byte from Stream

        public void MoveBlock()
        {
            var offset = BufferOffset + Pos - keepSizeBefore;
            // we need one additional byte, since MovePos moves on 1 byte.
            if (offset > 0)
                offset--;

            var numBytes = BufferOffset + StreamPos - offset;

            // check negative offset ????
            for (uint i = 0; i < numBytes; i++)
                BufferBase[i] = BufferBase[offset + i];
            BufferOffset -= offset;
        }

        public virtual void ReadBlock()
        {
            if (streamEndWasReached)
                return;
            while (true)
            {
                var size = (int) (0 - BufferOffset + BlockSize - StreamPos);
                if (size == 0)
                    return;
                var numReadBytes = stream.Read(BufferBase, (int) (BufferOffset + StreamPos), size);
                if (numReadBytes == 0)
                {
                    posLimit = StreamPos;
                    var pointerToPosition = BufferOffset + posLimit;
                    if (pointerToPosition > pointerToLastSafePosition)
                        posLimit = pointerToLastSafePosition - BufferOffset;

                    streamEndWasReached = true;
                    return;
                }

                StreamPos += (uint) numReadBytes;
                if (StreamPos >= Pos + keepSizeAfter)
                    posLimit = StreamPos - keepSizeAfter;
            }
        }

        private void Free()
        {
            BufferBase = null;
        }

        public void Create(uint keepSizeBefore, uint keepSizeAfter, uint keepSizeReserv)
        {
            this.keepSizeBefore = keepSizeBefore;
            this.keepSizeAfter = keepSizeAfter;
            var blockSize = keepSizeBefore + keepSizeAfter + keepSizeReserv;
            if (BufferBase == null || BlockSize != blockSize)
            {
                Free();
                BlockSize = blockSize;
                BufferBase = new byte[BlockSize];
            }

            pointerToLastSafePosition = BlockSize - keepSizeAfter;
        }

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
            BufferOffset = 0;
            Pos = 0;
            StreamPos = 0;
            streamEndWasReached = false;
            ReadBlock();
        }

        public void MovePos()
        {
            Pos++;
            if (Pos <= posLimit)
                return;
            var pointerToPosition = BufferOffset + Pos;
            if (pointerToPosition > pointerToLastSafePosition)
                MoveBlock();
            ReadBlock();
        }

        public byte GetIndexByte(int index)
        {
            return BufferBase[BufferOffset + Pos + index];
        }

        // index + limit have not to exceed _keepSizeAfter;
        public uint GetMatchLen(int index, uint distance, uint limit)
        {
            if (streamEndWasReached)
                if (Pos + index + limit > StreamPos)
                    limit = StreamPos - (uint) (Pos + index);
            distance++;
            // Byte *pby = _buffer + (size_t)_pos + index;
            var pby = BufferOffset + Pos + (uint) index;

            uint i;
            for (i = 0; i < limit && BufferBase[pby + i] == BufferBase[pby + i - distance]; i++) ;
            return i;
        }

        public uint GetNumAvailableBytes()
        {
            return StreamPos - Pos;
        }

        public void ReduceOffsets(int subValue)
        {
            BufferOffset += (uint) subValue;
            posLimit -= (uint) subValue;
            Pos -= (uint) subValue;
            StreamPos -= (uint) subValue;
        }
    }
}