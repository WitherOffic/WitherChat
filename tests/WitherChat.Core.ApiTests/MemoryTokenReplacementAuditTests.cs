using System.Reflection;
using WitherChat.Core.Services;
using Xunit;
namespace WitherChat.Core.ApiTests;
public sealed class MemoryTokenReplacementAuditTests
{
    [Theory]
    [InlineData(0, 32)]
    [InlineData(32, 64)]
    [InlineData(64, 0)]
    [InlineData(64, 64)]
    public void ReplacingFallbackTokenClearsTheOldOwnedCopyAndPreservesCallerBuffers(int oldLength, int newLength)
    {
        var type = typeof(SecureTokenStoreFactory).GetNestedType("MemoryTokenStore", BindingFlags.NonPublic)!;
        var store = (ISecureTokenStore)Activator.CreateInstance(type, true)!;
        var field = type.GetField("_data", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var oldInput = Enumerable.Repeat((byte)0x51, oldLength).ToArray();
        var newInput = Enumerable.Repeat((byte)0x72, newLength).ToArray();
        try
        {
            Assert.True(store.TrySave(oldInput));
            var oldOwned = (byte[])field.GetValue(store)!;
            if (oldLength > 0) Assert.NotSame(oldInput, oldOwned);
            Assert.True(store.TrySave(newInput));
            Assert.All(oldOwned, b => Assert.Equal((byte)0, b));
            Assert.All(oldInput, b => Assert.Equal((byte)0x51, b));
            Assert.All(newInput, b => Assert.Equal((byte)0x72, b));
            Assert.Equal(newInput, store.Load());
        }
        finally { store.Clear(); }
    }
}
