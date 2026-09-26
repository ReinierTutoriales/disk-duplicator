using System.Buffers;

namespace RepartoCopier.Core;

/// <summary>
/// Reusable aligned private payload for a lagging FAN-OUT destination.
/// The backing array is pooled, pinned only for the lifetime of the spill block,
/// and returned deterministically when the destination releases it.
/// </summary>
internal sealed class FanoutSpillBlock : IDisposable
{
    private SourceBufferLease? _lease;

    private FanoutSpillBlock(SourceBufferLease lease, int length)
    {
        _lease = lease;
        Length = length;
    }

    internal int Length { get; }
    internal ReadOnlyMemory<byte> Memory =>
        (_lease ?? throw new ObjectDisposedException(nameof(FanoutSpillBlock))).Memory[..Length];

    internal bool IsAlignedFor(int alignment) =>
        (_lease ?? throw new ObjectDisposedException(nameof(FanoutSpillBlock))).IsAlignedFor(alignment);

    internal static FanoutSpillBlock CopyFrom(ReadOnlyMemory<byte> source, int alignment)
    {
        if (source.IsEmpty) throw new ArgumentOutOfRangeException(nameof(source));
        var lease = SourceBufferLease.RentAligned(source.Length, alignment);
        try
        {
            source.CopyTo(lease.Memory);
            return new FanoutSpillBlock(lease, source.Length);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public void Dispose() => Interlocked.Exchange(ref _lease, null)?.Dispose();
}
