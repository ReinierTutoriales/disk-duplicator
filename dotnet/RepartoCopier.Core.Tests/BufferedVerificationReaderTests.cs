using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class BufferedVerificationReaderTests
{
    [TestMethod]
    public async Task FragmentedReadsFillBlockAtCorrectOffsets()
    {
        var payload = Enumerable.Range(0, 31).Select(value => (byte)value).ToArray();
        var buffer = new byte[17];
        var offsets = new List<long>();
        var read = await BufferedVerificationReader.FillBlockAsync(payload, buffer, 5,
            (data, remaining, offset, _) =>
            {
                offsets.Add(offset);
                var count = Math.Min(3, remaining.Length);
                data.AsMemory((int)offset, count).CopyTo(remaining);
                return ValueTask.FromResult(count);
            }, CancellationToken.None);

        Assert.AreEqual(buffer.Length, read);
        CollectionAssert.AreEqual(payload[5..22], buffer);
        CollectionAssert.AreEqual(new long[] { 5, 8, 11, 14, 17, 20 }, offsets);
    }

    [TestMethod]
    public async Task TrueEofReturnsPartialCountInsteadOfInventingBytes()
    {
        var buffer = Enumerable.Repeat((byte)255, 9).ToArray();
        var read = await BufferedVerificationReader.FillBlockAsync(new byte[] { 1, 2, 3, 4 }, buffer, 0,
            static (data, remaining, offset, _) =>
            {
                var count = Math.Min(2, data.Length - (int)offset);
                data.AsMemory((int)offset, count).CopyTo(remaining);
                return ValueTask.FromResult(count);
            }, CancellationToken.None);

        Assert.AreEqual(4, read);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 255, 255, 255, 255, 255 }, buffer);
    }

    [TestMethod]
    public async Task CancellationBetweenPartialReadsStopsWithoutFurtherIo()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await BufferedVerificationReader.FillBlockAsync(0, new byte[8], 0,
                (_, remaining, _, _) =>
                {
                    calls++;
                    remaining.Span[0] = 42;
                    cancellation.Cancel();
                    return ValueTask.FromResult(1);
                }, cancellation.Token));
        Assert.AreEqual(1, calls);
    }
}
