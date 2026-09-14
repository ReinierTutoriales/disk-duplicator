using System.Buffers.Binary;

namespace RepartoCopier.Core;

internal static class FastCrc32
{
    private const uint Polynomial = 0xEDB88320u;
    private static readonly uint[][] Tables = BuildTables();

    internal static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        var offset = 0;

        while (offset + 8 <= data.Length)
        {
            crc ^= BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
            crc =
                Tables[7][(byte)crc] ^
                Tables[6][(byte)(crc >> 8)] ^
                Tables[5][(byte)(crc >> 16)] ^
                Tables[4][(byte)(crc >> 24)] ^
                Tables[3][data[offset + 4]] ^
                Tables[2][data[offset + 5]] ^
                Tables[1][data[offset + 6]] ^
                Tables[0][data[offset + 7]];
            offset += 8;
        }

        for (; offset < data.Length; offset++)
            crc = Tables[0][(byte)(crc ^ data[offset])] ^ (crc >> 8);

        return ~crc;
    }

    private static uint[][] BuildTables()
    {
        var tables = new uint[8][];
        tables[0] = new uint[256];

        for (uint i = 0; i < tables[0].Length; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? (value >> 1) ^ Polynomial : value >> 1;
            tables[0][i] = value;
        }

        for (var slice = 1; slice < tables.Length; slice++)
        {
            tables[slice] = new uint[256];
            for (var i = 0; i < tables[slice].Length; i++)
            {
                var previous = tables[slice - 1][i];
                tables[slice][i] = (previous >> 8) ^ tables[0][(byte)previous];
            }
        }

        return tables;
    }
}

internal readonly record struct VerificationBlock(int Length, uint Crc32);
internal sealed record VerificationPlan(long Length, IReadOnlyList<VerificationBlock> Blocks);
