using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI;
using Windows.UI.ViewManagement;
using LumINE.Services.Vyline;

namespace LumINE
{
    sealed partial class App : Application
    {
        private bool _windowObserved;
        private bool _bootstrapRunning;
        public App()
        {
            this.InitializeComponent();
            this.UnhandledException += (s, args) => LumINE.Utils.CrashDiagnostics.Record("Unhandled", args.Exception ?? new Exception(args.Message));
            this.Suspending += OnSuspending;
            this.EnteredBackground += (s, args) =>
            {
                BackgroundNotifications.IsForeground = false;
                LumINE.Utils.ForegroundUi.Pause();
            };
            this.LeavingBackground += (s, args) => ScheduleForegroundReady();
        }

        protected override void OnLaunched(Windows.ApplicationModel.Activation.LaunchActivatedEventArgs e)
        {
            ActivateForeground(e.Arguments);
        }

        protected override void OnActivated(Windows.ApplicationModel.Activation.IActivatedEventArgs args)
        {
            var toast = args as Windows.ApplicationModel.Activation.ToastNotificationActivatedEventArgs;
            if (toast != null) ActivateForeground(toast.Argument);
            else base.OnActivated(args);
        }

        private void ActivateForeground(string arguments)
        {
            LumINE.Utils.NotificationNavigation.Receive(arguments);
            LumINE.Utils.ForegroundUi.Pause();
            BackgroundNotifications.IsForeground = true;
            System.Diagnostics.Debug.WriteLine("[App] OnLaunched entered");
            try
            {
                var statusBar = StatusBar.GetForCurrentView();
                statusBar.BackgroundColor = Colors.White;
                statusBar.BackgroundOpacity = 1.0;
                statusBar.ForegroundColor = Colors.Black;
                var ignoredStatusBar = statusBar.ShowAsync();
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[App] status bar color unavailable: " + ex.Message); }
            var window = Window.Current;
            if (!_windowObserved)
            {
                _windowObserved = true;
                window.VisibilityChanged += (s, args) =>
                {
                    if (args.Visible) ScheduleForegroundReady();
                    else
                    {
                        BackgroundNotifications.IsForeground = false;
                        LumINE.Utils.ForegroundUi.Pause();
                    }
                };
            }
            Frame rootFrame = window.Content as Frame;

            if (rootFrame == null)
            {
                rootFrame = new Frame();
                rootFrame.NavigationFailed += OnNavigationFailed;

                Window.Current.Content = rootFrame;
            }

            System.Diagnostics.Debug.WriteLine("[App] activating window");
            if (LumINE.Services.AppSettings.RequireHello)
            {
                if (!(rootFrame.Content is Views.UnlockPage)) rootFrame.Navigate(typeof(Views.UnlockPage), arguments);
                Window.Current.Activate();
                ScheduleForegroundReady();
                return;
            }
            Window.Current.Activate();
            ScheduleForegroundReady();

            if (!_bootstrapRunning && LumINE.Utils.NotificationNavigation.OpenPending(rootFrame)) return;

            if (rootFrame.Content == null)
            {
                // Resolve saved login before choosing the first page. This
                // avoids briefly showing the login form on normal launches.
                System.Diagnostics.Debug.WriteLine("[App] restoring session before first navigation");
                BootstrapAsync(rootFrame, arguments);
            }
        }

        private async void ScheduleForegroundReady()
        {
            try
            {
                var window = Window.Current;
                if (window == null) return;
                int generation = LumINE.Utils.ForegroundUi.Generation;
                await window.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, () =>
                {
                    if (!window.Visible) return;
                    BackgroundNotifications.IsForeground = true;
                    if (!LumINE.Utils.ForegroundUi.Resume(generation)) return;
                    ResumeServicesAsync();
                });
            }
            catch (Exception ex) { LumINE.Utils.CrashDiagnostics.Record("Foreground.Ready", ex); }
        }

        private async void ResumeServicesAsync()
        {
            try
            {
                // A live session/listener belongs to the same process. Reuse it
                // and drain buffered events only after its window is ready.
                var session = SessionManager.Current;
                if (session != null && !session.Disabled)
                {
                    session.EnsureSyncStarted();
                    await session.Sync.PollIncomingAsync();
                }
                await BackgroundNotifications.EnsureRealtimeAsync();
            }
            catch (Exception ex) { LumINE.Utils.CrashDiagnostics.Record("Foreground.Services", ex); }
        }

        private async void BootstrapAsync(Frame rootFrame, string arguments)
        {
            if (_bootstrapRunning) return;
            _bootstrapRunning = true;
            try
            {
                bool restored = false;
                try
                {
                    System.Diagnostics.Debug.WriteLine("[App] session restore entered");
                    restored = await SessionManager.TryRestoreAsync(LumINE.Utils.NotificationNavigation.PendingAccountId);
                    // A notification can arrive while a normal launch is awaiting
                    // storage. Keep one restore in flight and use the latest tap.
                    string account = LumINE.Utils.NotificationNavigation.PendingAccountId;
                    if (account != null && (!restored || SessionManager.Current == null || SessionManager.Current.AccountId != account))
                        restored = await SessionManager.TryRestoreAsync(account);
                    System.Diagnostics.Debug.WriteLine("[App] session restore finished: " + restored);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[App] restore failed: " + ex.Message);
                }

                await rootFrame.Dispatcher.RunAsync(
                    Windows.UI.Core.CoreDispatcherPriority.Normal,
                    () =>
                    {
                        if (rootFrame.Content != null) return;
                        if (restored && LumINE.Utils.NotificationNavigation.OpenPending(rootFrame)) return;
                        rootFrame.Navigate(restored ? typeof(Views.ChatListPage) : typeof(Views.LoginPage), arguments);
                    });
            }
            catch (Exception ex) { LumINE.Utils.CrashDiagnostics.Record("Launch.Navigation", ex); }
            finally { _bootstrapRunning = false; }
        }

        void OnNavigationFailed(object sender, Windows.UI.Xaml.Navigation.NavigationFailedEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("[App] navigation failed: " + e.SourcePageType.FullName + " / " + e.Exception);
            throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
        }

        private void OnSuspending(object sender, Windows.ApplicationModel.SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            deferral.Complete();
        }

        protected override async void OnBackgroundActivated(Windows.ApplicationModel.Activation.BackgroundActivatedEventArgs args)
        {
            var task = args.TaskInstance;
            var deferral = task.GetDeferral();
            using (var cancellation = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20)))
            {
                Windows.ApplicationModel.Background.BackgroundTaskCanceledEventHandler canceled = (s, reason) => cancellation.Cancel();
                task.Canceled += canceled;
                try
                {
                    if (BackgroundNotifications.IsNotificationTask(task.Task.Name))
                        await BackgroundNotifications.RunNotificationTaskAsync(task.Task.Name, cancellation.Token);
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Notifications] background check failed: " + ex.Message); }
                finally { task.Canceled -= canceled; deferral.Complete(); }
            }
        }
    }
}
