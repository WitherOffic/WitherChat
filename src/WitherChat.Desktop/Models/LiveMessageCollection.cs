using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace WitherChat.Desktop.Models;

public sealed class LiveMessageCollection<T> : ObservableCollection<T>
{
    public bool IsTrimming { get; private set; }
    public bool IsBatchUpdating { get; private set; }

    public void RemoveFirst(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        count = Math.Min(count, Count);
        if (count == 0)
        {
            return;
        }

        CheckReentrancy();
        var removed = Items.Take(count).ToList();
        if (Items is List<T> list)
        {
            list.RemoveRange(0, count);
        }
        else
        {
            for (var index = 0; index < count; index++)
            {
                Items.RemoveAt(0);
            }
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Remove,
            removed,
            0));
    }

    public void AppendBatch(IReadOnlyList<T> items, int maximumCount)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCount, 1);
        if (items.Count == 0)
        {
            return;
        }

        CheckReentrancy();
        var sourceStart = Math.Max(0, items.Count - maximumCount);
        var incomingCount = items.Count - sourceStart;
        var removalCount = Math.Max(0, Count + incomingCount - maximumCount);

        if (removalCount > 0)
        {
            if (Items is List<T> list)
            {
                list.RemoveRange(0, removalCount);
            }
            else
            {
                for (var index = 0; index < removalCount; index++)
                {
                    Items.RemoveAt(0);
                }
            }
        }

        var added = items.Skip(sourceStart).ToList();
        var startIndex = Count;
        if (Items is List<T> listItems)
        {
            listItems.AddRange(added);
        }
        else
        {
            foreach (var item in added)
            {
                Items.Add(item);
            }
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        if (removalCount > 0)
        {
            // Publish trimming and appending as one atomic visual update. Raising
            // separate Remove/Add events makes a virtualized chat briefly lay out
            // the shortened list between both operations, which produces visible
            // up/down flashing once the message limit has been reached.
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Reset));
        }
        else
        {
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Add,
                added,
                startIndex));
        }
    }

    public void TrimToMaximum(int maximumCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCount, 1);
        var removalCount = Math.Max(0, Count - maximumCount);
        if (removalCount == 0)
        {
            return;
        }

        CheckReentrancy();
        var removed = Items.Take(removalCount).ToList();
        if (Items is List<T> list)
        {
            list.RemoveRange(0, removalCount);
        }
        else
        {
            for (var index = 0; index < removalCount; index++)
            {
                Items.RemoveAt(0);
            }
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Remove,
            removed,
            0));
    }

    public void AppendRangeAndTrim(IReadOnlyList<T> items, int maximumCount)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCount, 1);
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

            var removalCount = Math.Max(0, Count - maximumCount);
            if (removalCount > 0)
            {
                var retained = Items.Skip(removalCount).ToArray();
                Items.Clear();
                foreach (var item in retained)
                {
                    Items.Add(item);
                }
            }

            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Reset));
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
            var removed = Items.Take(count).ToList();
            if (Items is List<T> list)
            {
                list.RemoveRange(0, count);
            }
            else
            {
                for (var index = 0; index < count; index++)
                {
                    Items.RemoveAt(0);
                }
            }

            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Remove,
                removed,
                0));
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
    private const int TrimHeadroom = 150;

    public static int GetTrimTrigger(int configuredLimit) =>
        configuredLimit > int.MaxValue - TrimHeadroom
            ? int.MaxValue
            : configuredLimit + TrimHeadroom;
}
