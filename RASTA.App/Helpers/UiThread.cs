using System;
using System.Windows;

namespace RASTA.App.Helpers
{
    /// <summary>
    /// Marshals an action onto the WPF dispatcher thread, tolerating the window during app
    /// shutdown where Application.Current goes null and/or its Dispatcher stops accepting
    /// new work before every background loop has actually stopped (TelescopeService's poll
    /// loop, UsbWatcherService's debounce timer, ...). Without this guard, a
    /// PropertyChanged notification arriving in that window throws a
    /// NullReferenceException instead of harmlessly being dropped - see
    /// NavigationViewModel/PrepareViewModel's SdrConnected/TelescopeConnected handlers,
    /// which used to call Application.Current.Dispatcher.Invoke directly.
    /// </summary>
    public static class UiThread
    {
        public static void SafeInvoke(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
                return;

            dispatcher.Invoke(action);
        }
    }
}
