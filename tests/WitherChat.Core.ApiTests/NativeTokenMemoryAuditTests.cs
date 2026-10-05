using System.Runtime.InteropServices;
using WitherChat.Core.Services;
using Xunit;

namespace WitherChat.Core.ApiTests;

public sealed class NativeTokenMemoryAuditTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    [InlineData(64)]
    public void NativeBufferIsClearedWithoutWritingOutsideItsLength(int length)
    {
        var pointer = Marshal.AllocHGlobal(64);
        try
        {
            var original = Enumerable.Repeat((byte)0x7b, 64).ToArray();
            Marshal.Copy(original, 0, pointer, original.Length);
            LocalDataProtection.ZeroNativeMemory(pointer, length);
            var actual = new byte[64];
            Marshal.Copy(pointer, actual, 0, actual.Length);
            Assert.All(actual.Take(length), value => Assert.Equal((byte)0, value));
            Assert.All(actual.Skip(length), value => Assert.Equal((byte)0x7b, value));
        }
        finally { LocalDataProtection.ZeroNativeMemory(pointer, 64); Marshal.FreeHGlobal(pointer); }
    }

    [Fact]
    public void EmptyNativeBufferCleanupIsSafe() => LocalDataProtection.ZeroNativeMemory(IntPtr.Zero, 64);
}
