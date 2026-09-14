using System.Buffers.Binary;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace RepartoCopier.Core;

internal static class FastCrc32
{
    // Reflected CRC-32C/Castagnoli polynomial. The checksum is internal to a
    // single copy/verify execution; it is not part of recovery or any persisted format.
    private const uint Polynomial = 0x82F63B78u;
    private static readonly uint[][] Tables = BuildTables();

    internal static bool IsHardwareAccelerated =>
        Sse42.IsSupported || Crc32.IsSupported;

    internal static uint Compute(ReadOnlySpan<byte> data)
    {
        if (Sse42.IsSupported)
            return ComputeSse42(data);
        if (Crc32.IsSupported)
            return ComputeArm(data);
        return ComputeSoftware(data);
    }

    internal static uint ComputeSoftware(ReadOnlySpan<byte> data)
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

    internal static uint ComputeHardware(ReadOnlySpan<byte> data)
    {
        if (Sse42.IsSupported)
            return ComputeSse42(data);
        if (Crc32.IsSupported)
            return ComputeArm(data);
        throw new PlatformNotSupportedException("CRC32C hardware acceleration is not available on this CPU.");
    }

    private static uint ComputeSse42(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        var offset = 0;

        if (Sse42.X64.IsSupported)
        {
            ulong crc64 = crc;
            while (offset + sizeof(ulong) <= data.Length)
            {
                crc64 = Sse42.X64.Crc32(
                    crc64,
                    BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, sizeof(ulong))));
                offset += sizeof(ulong);
            }
            crc = (uint)crc64;
        }

        while (offset + sizeof(uint) <= data.Length)
        {
            crc = Sse42.Crc32(
                crc,
                BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, sizeof(uint))));
            offset += sizeof(uint);
        }

        while (offset < data.Length)
            crc = Sse42.Crc32(crc, data[offset++]);

        return ~crc;
    }

    private static uint ComputeArm(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        var offset = 0;

        if (Crc32.Arm64.IsSupported)
        {
            while (offset + sizeof(ulong) <= data.Length)
            {
                crc = Crc32.Arm64.ComputeCrc32C(
                    crc,
                    BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, sizeof(ulong))));
                offset += sizeof(ulong);
            }
        }

        while (offset + sizeof(uint) <= data.Length)
        {
            crc = Crc32.ComputeCrc32C(
                crc,
                BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, sizeof(uint))));
            offset += sizeof(uint);
        }

        while (offset < data.Length)
            crc = Crc32.ComputeCrc32C(crc, data[offset++]);

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
