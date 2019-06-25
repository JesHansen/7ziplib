using System;
using System.IO;

namespace CompressionLzma
{
    public class CompressionHelper
    {
        public byte[] Compress(byte[] uncompressedBytes)
        {
            var encoder = new SevenZip.Compression.LZMA.Encoder();
            using (var input = new MemoryStream(uncompressedBytes))
            using (var output = new MemoryStream())
            {
                encoder.WriteCoderProperties(output);
                output.Write(BitConverter.GetBytes(input.Length), 0, 8);
                encoder.Code(input, output, input.Length, -1, null);
                output.Flush();
                return output.ToArray();
            }
        }

        public byte[] Decompress(byte[] compressedBytes)
        {
            using (var input = new MemoryStream(compressedBytes))
            using (var output = new MemoryStream())
            {
                var decoder = new SevenZip.Compression.LZMA.Decoder();
                var properties = new byte[5];
                input.Read(properties, 0, 5);
                var fileLengthBytes = new byte[8];
                input.Read(fileLengthBytes, 0, 8);
                var fileLength = BitConverter.ToInt64(fileLengthBytes, 0);
                decoder.SetDecoderProperties(properties);
                decoder.Code(input, output, input.Length, fileLength, null);
                output.Flush();
                return output.ToArray();
            }
        }
    }
}
