using System.Runtime.InteropServices;

namespace GhostShell.Terminal.Tests;

/// <summary>
/// Why the resources directory is published with <c>setenv</c> and not only with
/// <see cref="Environment.SetEnvironmentVariable(string, string)"/>.
///
/// The terminal engine reads <c>GHOSTTY_RESOURCES_DIR</c> from native code. On
/// Unix the managed setter updates only the runtime's own copy, so the engine
/// found no resources directory, disabled shell integration, and without prompt
/// markers could never tell an idle terminal from a busy one — which is why
/// closing an idle terminal still asked for confirmation.
/// </summary>
public sealed class NativeEnvironmentVisibilityTests
{
    [DllImport("libc", EntryPoint = "getenv", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetNativeEnvironmentVariable(string name);

    [DllImport("libc", EntryPoint = "setenv", CharSet = CharSet.Ansi)]
    private static extern int SetNativeEnvironmentVariable(string name, string value, int overwrite);

    private static string? NativeValue(string name)
    {
        var pointer = GetNativeEnvironmentVariable(name);
        return pointer == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(pointer);
    }

    [Fact]
    public void The_managed_setter_alone_does_not_reach_native_code()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var name = $"GHOSTSHELL_PROBE_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(name, "managed-only");

        Assert.Equal("managed-only", Environment.GetEnvironmentVariable(name));
        Assert.Null(NativeValue(name));
    }

    [Fact]
    public void Setenv_is_what_native_code_can_actually_see()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var name = $"GHOSTSHELL_PROBE_{Guid.NewGuid():N}";
        Assert.Equal(0, SetNativeEnvironmentVariable(name, "native", 1));

        Assert.Equal("native", NativeValue(name));
    }
}
