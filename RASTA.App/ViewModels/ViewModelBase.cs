using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections;
using System.ComponentModel;

namespace RASTA.App.ViewModels
{
    public abstract class ViewModelBase : ObservableObject, INotifyDataErrorInfo
    {
        private readonly Dictionary<string, List<string>> _errors = new();

        public bool HasErrors => _errors.Any();

        public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

        public IEnumerable GetErrors(string? propertyName)
        {
            if (propertyName != null && _errors.ContainsKey(propertyName))
                return _errors[propertyName];

            return Enumerable.Empty<string>();
        }

        protected void AddError(string propertyName, string error)
        {
            if (!_errors.ContainsKey(propertyName))
                _errors[propertyName] = new List<string>();

            if (!_errors[propertyName].Contains(error))
            {
                _errors[propertyName].Add(error);
                ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(propertyName));
            }

            NotifyCommandsOfCanExecuteChanged();
        }

        protected void ClearErrors(string propertyName)
        {
            if (_errors.Remove(propertyName))
                ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(propertyName));

            NotifyCommandsOfCanExecuteChanged();
        }

        /// <summary>
        /// Override this in derived ViewModels to notify commands when validation changes.
        /// </summary>
        protected virtual void NotifyCommandsOfCanExecuteChanged()
        {
            // Derived ViewModels override this to call:
            // SomeCommand.NotifyCanExecuteChanged();
        }
    }
}
