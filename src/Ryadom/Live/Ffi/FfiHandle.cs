namespace Ryadom.Live.Ffi;

/// <summary>Владение объектом на стороне livekit-ffi. Отпущенный — освобождён там же.</summary>
internal sealed class FfiHandle(ulong id) : IDisposable
{
    private ulong _id = id;

    public ulong Id => _id;

    public void Dispose()
    {
        var id = Interlocked.Exchange(ref _id, 0);
        if (id != 0) Native.DropHandle(id);
    }
}
