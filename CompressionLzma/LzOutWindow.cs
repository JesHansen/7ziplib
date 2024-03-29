// LzOutWindow.cs

using System.IO;

namespace SevenZip.Compression.LZ
{
    public class OutWindow
    {
        private byte[] buffer;
        private uint pos;
        private Stream stream;
        private uint streamPos;

        public uint TrainSize;
        private uint windowSize;

        public void Create(uint windowSize)
        {
            if (this.windowSize != windowSize)
                buffer = new byte[windowSize];
            this.windowSize = windowSize;
            pos = 0;
            streamPos = 0;
        }

        public void Init(Stream stream, bool solid)
        {
            ReleaseStream();
            this.stream = stream;
            if (solid)
                return;
            streamPos = 0;
            pos = 0;
            TrainSize = 0;
        }

        public bool Train(Stream stream)
        {
            var len = stream.Length;
            var size = len < windowSize ? (uint) len : windowSize;
            TrainSize = size;
            stream.Position = len - size;
            streamPos = pos = 0;
            while (size > 0)
            {
                var curSize = windowSize - pos;
                if (size < curSize)
                    curSize = size;
                var numReadBytes = stream.Read(buffer, (int) pos, (int) curSize);
                if (numReadBytes == 0)
                    return false;
                size -= (uint) numReadBytes;
                pos += (uint) numReadBytes;
                streamPos += (uint) numReadBytes;
                if (pos == windowSize)
                    streamPos = pos = 0;
            }

            return true;
        }

        public void ReleaseStream()
        {
            Flush();
            stream = null;
        }

        public void Flush()
        {
            var size = pos - streamPos;
            if (size == 0)
                return;
            stream.Write(buffer, (int) streamPos, (int) size);
            if (pos >= windowSize)
                pos = 0;
            streamPos = pos;
        }

        public void CopyBlock(uint distance, uint len)
        {
            var pos = this.pos - distance - 1;
            if (pos >= windowSize)
                pos += windowSize;
            for (; len > 0; len--)
            {
                if (pos >= windowSize)
                    pos = 0;
                buffer[this.pos++] = buffer[pos++];
                if (this.pos >= windowSize)
                    Flush();
            }
        }

        public void PutByte(byte b)
        {
            buffer[pos++] = b;
            if (pos >= windowSize)
                Flush();
        }

        public byte GetByte(uint distance)
        {
            var pos = this.pos - distance - 1;
            if (pos >= windowSize)
                pos += windowSize;
            return buffer[pos];
        }
    }
}