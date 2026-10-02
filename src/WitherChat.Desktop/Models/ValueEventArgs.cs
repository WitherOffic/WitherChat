namespace WitherChat.Desktop.Models;

public sealed class ValueEventArgs<T>(T value) : EventArgs
{
    public T Value { get; } = value;
}
