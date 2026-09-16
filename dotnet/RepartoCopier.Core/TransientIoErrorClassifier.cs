namespace RepartoCopier.Core;

/// <summary>
/// Extracts and classifies native Win32 I/O failures without manufacturing
/// HRESULT values. Direct I/O exceptions expose their native code explicitly;
/// generic IOException values are accepted only for FACILITY_WIN32 HRESULTs.
/// </summary>
internal static class TransientIoErrorClassifier
{
    internal static bool TryGetNativeCode(Exception error, out int code)
    {
        ArgumentNullException.ThrowIfNull(error);
        switch (error)
        {
            case DirectIoDestinationWriter.DirectIoWriteException directWrite:
                code = directWrite.NativeErrorCode;
                return code > 0;
            case DirectIoSourceReader.DirectIoReadException directRead:
                code = directRead.NativeErrorCode;
                return code > 0;
            case IOException io:
            {
                var hr = unchecked((uint)io.HResult);
                if ((hr & 0xFFFF0000u) == 0x80070000u)
                {
                    code = (int)(hr & 0xFFFFu);
                    return code > 0;
                }
                break;
            }
        }

        code = 0;
        return false;
    }

    internal static int GetNativeCodeOrZero(Exception error) =>
        TryGetNativeCode(error, out var code) ? code : 0;

    internal static bool IsTransient(Exception error)
    {
        if (!TryGetNativeCode(error, out var code))
            return false;
        return code is
            32 or 33 or 54 or 64 or 121 or
            1231 or 1232 or 1233 or 1236 or 1237;
    }

    internal static bool IsDirectFallbackable(Exception error)
    {
        if (!TryGetNativeCode(error, out var code))
            return false;
        return code is 1 or 5 or 50 or 87;
    }
}
