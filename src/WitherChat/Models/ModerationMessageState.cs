namespace WitherChat.Models;

public enum ModerationMessageState
{
    None = 0,
    Deleted = 1,
    TimedOut = 2,
    Banned = 3,
    AutoModDenied = 4,
    RemovedByModeration = 5
}
