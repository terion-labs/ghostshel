namespace Exclr8Cef.Native;

internal unsafe partial struct excef_macos_accelerated_frame
{
    public void* io_surface;
    public void* ready_event;

    [NativeTypeName("uint64_t")]
    public ulong ready_value;

    public int width;
    public int height;
    public int format;
}
