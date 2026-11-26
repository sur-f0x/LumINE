using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using LumINE.Models;

namespace LumINE.ViewModels
{
    public sealed class MessageCollection : ObservableCollection<Message>
    {
        public bool ApplySnapshot(IReadOnlyList<Message> desired)
        {
            int prefix = 0;
            while (prefix < Count && prefix < desired.Count && object.ReferenceEquals(this[prefix], desired[prefix])) prefix++;
            if (prefix == Count && prefix == desired.Count) return false;
            CheckReentrancy();
            // Keep lightweight live appends incremental. UWP does not support
            // multi-item Add notifications; publish a single Reset for history.
            if (Count > 0 && prefix == Count && desired.Count - Count <= 8)
            {
                for (int i = prefix; i < desired.Count; i++) Add(desired[i]);
            }
            else if (Count == desired.Count && prefix < Count)
            {
                int end = Count - 1;
                while (end > prefix && object.ReferenceEquals(this[end], desired[end])) end--;
                if (end == prefix) { this[prefix] = desired[prefix]; return true; }
                ReplaceAll(desired);
            }
            else ReplaceAll(desired);
            return true;
        }

        private void ReplaceAll(IReadOnlyList<Message> desired)
        {
            Items.Clear();
            for (int i = 0; i < desired.Count; i++) Items.Add(desired[i]);
            OnPropertyChanged(new PropertyChangedEventArgs("Count"));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}
