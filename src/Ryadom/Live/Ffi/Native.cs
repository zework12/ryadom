using System.Runtime.InteropServices;

namespace Ryadom.Live.Ffi;

/// <summary>Три вызова livekit_ffi.dll — всё остальное ходит protobuf-сообщениями.</summary>
internal static unsafe partial class Native
{
    private const string Lib = "livekit_ffi";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void EventCallback(byte* data, nuint size);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "livekit_ffi_initialize")]
    public static extern ulong Initialize(
        EventCallback callback,
        [MarshalAs(UnmanagedType.U1)] bool captureLogs,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string sdk,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string sdkVersion);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "livekit_ffi_request")]
    public static extern ulong Request(byte* data, nuint length, out byte* response, out nuint responseLength);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "livekit_ffi_drop_handle")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool DropHandle(ulong handle);
}
