using System.Runtime.CompilerServices;

namespace Exclr8Cef.Native;

internal partial struct excef_versions
{
    [NativeTypeName("char[64]")]
    public _shim_version_e__FixedBuffer shim_version;

    [NativeTypeName("char[64]")]
    public _cef_version_e__FixedBuffer cef_version;

    [NativeTypeName("char[64]")]
    public _chromium_version_e__FixedBuffer chromium_version;

    [InlineArray(64)]
    public partial struct _shim_version_e__FixedBuffer
    {
        public sbyte e0;
    }

    [InlineArray(64)]
    public partial struct _cef_version_e__FixedBuffer
    {
        public sbyte e0;
    }

    [InlineArray(64)]
    public partial struct _chromium_version_e__FixedBuffer
    {
        public sbyte e0;
    }
}
