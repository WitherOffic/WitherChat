using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace WitherChat.ViewModels;

public sealed class LiveMessageCollection<T> : ObservableCollection<T>
{
    public bool IsTrimming { get; private set; }
    public bool IsBatchUpdating { get; private set; }

    public void AppendRangeAndTrim(IReadOnlyList<T> items, int maximumCount)
    {
        maximumCount = Math.Max(0, maximumCount);
        if (items.Count == 0 && Count <= maximumCount)
        {
            return;
        }

        CheckReentrancy();
        IsBatchUpdating = true;
        IsTrimming = Count + items.Count > maximumCount;
        try
        {
            foreach (var item in items)
            {
                Items.Add(item);
            }

            var removeCount = Math.Max(0, Count - maximumCount);
            if (removeCount > 0)
            {
                var retained = Items.Skip(removeCount).ToArray();
                Items.Clear();
                foreach (var item in retained)
                {
                    Items.Add(item);
                }
            }

            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
        finally
        {
            IsTrimming = false;
            IsBatchUpdating = false;
        }
    }

    public int RemoveOldestRange(int count)
    {
        count = Math.Clamp(count, 0, Count);
        if (count == 0)
        {
            return 0;
        }

        CheckReentrancy();
        IsTrimming = true;
        try
        {
            var retained = Items.Skip(count).ToArray();
            Items.Clear();
            foreach (var item in retained)
            {
                Items.Add(item);
            }

            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            return count;
        }
        finally
        {
            IsTrimming = false;
        }
    }

}

public static class LiveChatBufferPolicy
{
    public const int MinimumMessageLimit = 100;
    public const int MaximumMessageLimit = 5000;
    public const int RecentMessageAssetRefreshLimit = 500;
    private const int TrimHeadroom = 150;

    public static int GetTarget(int configuredLimit) =>
        Math.Clamp(configuredLimit, MinimumMessageLimit, MaximumMessageLimit);

    public static int GetTrimTrigger(int configuredLimit)
    {
        var target = GetTarget(configuredLimit);
        return target + TrimHeadroom;
    }

}
