using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace R2Cmd;

/// <summary>
/// An ObservableCollection that can swap its entire contents in one shot,
/// raising a single "Reset" notification instead of a Clear + one Add per item.
/// Used for the file panes: repopulating a folder with thousands of entries via
/// individual Add calls is O(n) UI notifications at best, and if the ListView's
/// CollectionView has active sort descriptions, WPF re-positions each item into
/// its sorted slot as it arrives, which is effectively O(n^2) for a full refresh.
/// A single Reset lets WPF re-read and re-sort the list once.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> newItems)
    {
        // Items is the protected backing IList<T> from Collection<T>; writing to it
        // directly (instead of the public Add/Clear) does not raise per-item events.
        Items.Clear();
        foreach (var item in newItems)
            Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
