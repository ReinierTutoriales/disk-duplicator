namespace RepartoCopier.Core;

/// <summary>
/// Classifies only failures for which retrying the same buffered write can
/// plausibly succeed without changing the request. Hardware/media errors such
/// as CRC (23) and I/O device error (1117) are deliberately not masked.
/// </summary>
internal static class TransientIoErrorClassifier
{
    internal static bool IsTransient(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is OperationCanceledException)
            return false;
        if (exception is not IOException io)
            return false;

        var code = io.HResult & 0xFFFF;
        return code is
            32 or   // ERROR_SHARING_VIOLATION
            33 or   // ERROR_LOCK_VIOLATION
            54 or   // ERROR_NETWORK_BUSY
            64 or   // ERROR_NETNAME_DELETED
            121 or  // ERROR_SEM_TIMEOUT
            1231 or // ERROR_NETWORK_UNREACHABLE
            1232 or // ERROR_HOST_UNREACHABLE
            1233 or // ERROR_PROTOCOL_UNREACHABLE
            1236 or // ERROR_CONNECTION_ABORTED
            1237;   // ERROR_RETRY
    }
}