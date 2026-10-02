namespace WitherChat.Core.Services;

public interface ISecureTokenStore
{
    bool IsPersistent { get; }
    byte[]? Load();
    bool TrySave(byte[] data);
    void Clear();
}
