namespace RepartoCopier.Core;

internal static class FastCrc32
{
    private const uint Polynomial = 0xEDB88320u;
    private static readonly uint[] Table = BuildTable();

    internal static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
            crc = Table[(byte)(crc ^ value)] ^ (crc >> 8);
        return ~crc;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? (value >> 1) ^ Polynomial : value >> 1;
            table[i] = value;
        }
        return table;
    }
}

internal readonly record struct VerificationBlock(int Length, uint Crc32);
internal sealed record VerificationPlan(long Length, IReadOnlyList<VerificationBlock> Blocks);
