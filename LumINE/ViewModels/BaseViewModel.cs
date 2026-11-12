using System;
using System.Collections.Generic;
using System.Text;

namespace LumINE.ViewModels
{
    public class BaseViewModel : System.ComponentModel.INotifyPropertyChanged
    {
        private int _busyCount;
        public bool IsBusy { get { return _busyCount > 0; } }

        // UI operations may overlap (history, refresh and send). One completion
        // must not hide the indicator while another operation is still running.
        public IDisposable BeginBusy()
        {
            if (++_busyCount == 1) OnPropertyChanged("IsBusy");
            return new BusyScope(this);
        }

        public async System.Threading.Tasks.Task RunBusyAsync(Func<System.Threading.Tasks.Task> operation)
        {
            using (BeginBusy()) await operation();
        }

        public async System.Threading.Tasks.Task<T> RunBusyAsync<T>(Func<System.Threading.Tasks.Task<T>> operation)
        {
            using (BeginBusy()) return await operation();
        }

        private sealed class BusyScope : IDisposable
        {
            private BaseViewModel _owner;
            public BusyScope(BaseViewModel owner) { _owner = owner; }
            public void Dispose()
            {
                var owner = _owner;
                if (owner == null) return;
                _owner = null;
                if (--owner._busyCount == 0) owner.OnPropertyChanged("IsBusy");
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
        }
    }
}
