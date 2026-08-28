using System;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Services;
using Windows.Devices.Geolocation;
using Windows.ApplicationModel.ExtendedExecution;
using Windows.Foundation.Metadata;
using Windows.Storage;
using Windows.System;

namespace Telegram.Notifications
{
    internal static class TelegramContinuousNotificationPoller
    {
        private static readonly object Gate = new object();
        private static CancellationTokenSource _cancellation;
        private static ExtendedExecutionSession _session;
        private static ExtendedExecutionSession _keepAliveSession;
        private static Geolocator _geolocator;
        private static bool _started;
        private static string _lastKeepAliveStatus = string.Empty;
        private static DateTime _lastLoopDiagnosticUtc = DateTime.MinValue;
        private static DateTime _lastPositionDiagnosticUtc = DateTime.MinValue;
        private static readonly SemaphoreSlim DiagnosticGate = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Serialises StartKeepAliveAsync.
        ///
        /// The "already active" check cannot do it alone: two awaits follow it, so two
        /// callers arriving together both find no session, both build a Geolocator, and
        /// the second overwrites the first. The first is then unreachable - still
        /// subscribed, still holding the location engine, and impossible to stop,
        /// because StopGeolocator only knows about the field. The log shows this
        /// happening: "Location status changed: Ready" arrives twice, once per
        /// orphaned tracker.
        ///
        /// The method is called from EnteredBackground, LeavingBackground, Resuming and
        /// the shell page, so overlapping calls are normal rather than exceptional.
        /// </summary>
        private static readonly SemaphoreSlim KeepAliveGate = new SemaphoreSlim(1, 1);
        private const string DiagnosticLogFileName = "telegram-bg-geo.log";

        public static string LastKeepAliveStatus
        {
            get { return _lastKeepAliveStatus ?? string.Empty; }
        }

        public static bool KeepAliveActive
        {
            get { return _keepAliveSession != null; }
        }

        public static void Diag(string message)
        {
            try
            {
                var ignored = WriteDiagnosticAsync(message);
            }
            catch
            {
            }
        }

        /// <summary>
        /// The tail of the background log, for the diagnostics block in Settings.
        ///
        /// Whether always-on is holding, was denied, or was revoked and by whom looks
        /// identical from the outside - notifications simply stop. The poller has been
        /// recording all of it here the whole time; this only makes it readable on the
        /// handset, where the failure actually happens.
        /// </summary>
        public static async Task<string> ReadDiagnosticsAsync(int lines)
        {
            await DiagnosticGate.WaitAsync();
            try
            {
                var item = await ApplicationData.Current.LocalFolder.TryGetItemAsync(DiagnosticLogFileName);
                var file = item as StorageFile;
                if (file == null) return "No background log yet.";

                var all = await FileIO.ReadLinesAsync(file);
                var start = all.Count > lines ? all.Count - lines : 0;
                var text = string.Join(Environment.NewLine, System.Linq.Enumerable.Skip(all, start));
                return string.IsNullOrEmpty(text) ? "No background log yet." : text;
            }
            catch (Exception ex)
            {
                return "Background log unavailable: " + ex.Message;
            }
            finally
            {
                DiagnosticGate.Release();
            }
        }

        private static async Task WriteDiagnosticAsync(string message)
        {
            await DiagnosticGate.WaitAsync();
            try
            {
                var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    DiagnosticLogFileName,
                    CreationCollisionOption.OpenIfExists);
                await FileIO.AppendTextAsync(file,
                    DateTime.UtcNow.ToString("o") + " " + (message ?? string.Empty) + "\r\n");
            }
            catch
            {
            }
            finally
            {
                DiagnosticGate.Release();
            }
        }

        public static void Start()
        {
            if (TelegramAppSettings.NotificationMode == TelegramNotificationMode.None)
            {
                Diag("Poller start skipped: notifications disabled.");
                Stop();
                return;
            }

            lock (Gate)
            {
                if (_started) return;
                _started = true;
                _cancellation = new CancellationTokenSource();
            }

            Diag("Poller started. mode=" + TelegramAppSettings.NotificationMode.ToString() + " keepAlive=" + KeepAliveActive.ToString() + Memory());
            MemoryManager.AppMemoryUsageIncreased += OnMemoryUsageIncreased;
            var ignored = RunLoopAsync(_cancellation.Token);
        }

        public static void Stop()
        {
            CancellationTokenSource cancellation = null;
            var wasStarted = false;
            lock (Gate)
            {
                wasStarted = _started;
                if (_started)
                {
                    _started = false;
                    cancellation = _cancellation;
                    _cancellation = null;
                }
            }

            if (cancellation != null)
                cancellation.Cancel();

            Diag(wasStarted ? "Poller stopped." : "Poller stop requested while not running.");
            CloseExtendedExecution();
            StopKeepAlive();
        }

        public static async Task EnterBackgroundAsync()
        {
            if (TelegramAppSettings.NotificationMode == TelegramNotificationMode.None)
            {
                Diag("EnterBackground: notifications disabled.");
                Stop();
                return;
            }

            Diag("EnterBackground: mode=" + TelegramAppSettings.NotificationMode.ToString() + Memory());
            Start();
            if (TelegramAppSettings.NotificationMode == TelegramNotificationMode.Always)
                await StartKeepAliveAsync();
            else
                await RequestExtendedExecutionAsync();
        }

        public static void LeaveBackground()
        {
            Diag("LeaveBackground: mode=" + TelegramAppSettings.NotificationMode.ToString() + " keepAlive=" + KeepAliveActive.ToString());
            CloseExtendedExecution();
            if (TelegramAppSettings.NotificationMode == TelegramNotificationMode.Always)
            {
                Start();
                return;
            }

            StopKeepAlive();
            Stop();
        }

        private static async Task RunLoopAsync(CancellationToken cancellationToken)
        {
            var lastNotificationPollUtc = DateTime.MinValue;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var mode = TelegramAppSettings.NotificationMode;
                    if (mode == TelegramNotificationMode.None)
                        break;

                    // A private call is delivered as updatePhoneCall, not as a chat message.
                    // Poll TDLib service updates frequently so an incoming phoneCallRequested
                    // is processed before Telegram turns the outgoing side into missed.
                    if (await TelegramService.Instance.IsAuthorizedAsync())
                        await TelegramService.Instance.PollServiceUpdatesAsync();

                    var interval = mode == TelegramNotificationMode.Always
                        ? TimeSpan.FromSeconds(6)
                        : (mode == TelegramNotificationMode.FixedSystem
                            ? TimeSpan.FromSeconds(60)
                            : TimeSpan.FromSeconds(35));

                    if (TelegramAppSettings.NotificationsEnabled &&
                        DateTime.UtcNow - lastNotificationPollUtc >= interval)
                    {
                        lastNotificationPollUtc = DateTime.UtcNow;
                        if (DateTime.UtcNow - _lastLoopDiagnosticUtc >= TimeSpan.FromMinutes(1))
                        {
                            _lastLoopDiagnosticUtc = DateTime.UtcNow;
                            Diag("PollAndNotify tick. mode=" + mode.ToString() + " keepAlive=" + KeepAliveActive.ToString() + Memory());
                        }
                        await TelegramNotificationRuntime.PollAndNotifyAsync();
                    }
                }
                catch (Exception ex)
                {
                    Diag("RunLoop failed: " + ex.Message);
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                }
                catch
                {
                }
            }

            Stop();
        }

        private static async Task RequestExtendedExecutionAsync()
        {
            try
            {
                CloseExtendedExecution();

                var session = new ExtendedExecutionSession();
                session.Reason = ExtendedExecutionReason.Unspecified;
                session.Description = "Checking Telegram messages";
                session.Revoked += OnExtendedExecutionRevoked;

                var result = await session.RequestExtensionAsync();
                Diag("Extended execution result=" + result.ToString());
                if (result == ExtendedExecutionResult.Allowed)
                {
                    _session = session;
                }
                else
                {
                    session.Dispose();
                }
            }
            catch
            {
            }
        }

        private static void OnExtendedExecutionRevoked(object sender, ExtendedExecutionRevokedEventArgs args)
        {
            CloseExtendedExecution();
        }

        private static void CloseExtendedExecution()
        {
            try
            {
                if (_session != null)
                {
                    _session.Revoked -= OnExtendedExecutionRevoked;
                    _session.Dispose();
                    _session = null;
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// How much of the app's memory budget is left, as a log fragment.
        ///
        /// A revoked session announces itself; a process killed for memory does not,
        /// and the log shows exactly that - ticks that simply stop, with the next line
        /// being a fresh "Poller started". Without a number against each entry there is
        /// no way to tell an OS policy decision from the app being over its budget, and
        /// on a 1 GB handset those need opposite fixes.
        /// </summary>
        private static string Memory()
        {
            try
            {
                var limit = MemoryManager.AppMemoryUsageLimit;
                var used = MemoryManager.AppMemoryUsage;
                return " mem=" + (used / 1024 / 1024).ToString() +
                       "/" + (limit / 1024 / 1024).ToString() + "MB" +
                       " level=" + MemoryManager.AppMemoryUsageLevel.ToString();
            }
            catch (Exception)
            {
                // Not worth failing a diagnostic over.
                return string.Empty;
            }
        }

        /// <summary>
        /// Reports the app crossing a memory threshold.
        ///
        /// This is the warning shot before termination: the level goes High or
        /// OverLimit and the process is reclaimed shortly after, too fast to log
        /// anything from the code that was running.
        /// </summary>
        private static void OnMemoryUsageIncreased(object sender, object e)
        {
            Diag("Memory level rose." + Memory());
        }

        public static async Task<bool> StartKeepAliveAsync()
        {
            await KeepAliveGate.WaitAsync();
            try
            {
                return await StartKeepAliveCoreAsync();
            }
            finally
            {
                KeepAliveGate.Release();
            }
        }

        private static async Task<bool> StartKeepAliveCoreAsync()
        {
            Diag("StartKeepAlive requested. mode=" + TelegramAppSettings.NotificationMode.ToString() + " active=" + KeepAliveActive.ToString());
            if (TelegramAppSettings.NotificationMode == TelegramNotificationMode.None)
            {
                StopKeepAlive();
                _lastKeepAliveStatus = "Always-on notifications are disabled.";
                Diag(_lastKeepAliveStatus);
                return false;
            }

            if (_keepAliveSession != null)
            {
                _lastKeepAliveStatus = "Always-on background session is already active.";
                Diag(_lastKeepAliveStatus);
                return true;
            }

            bool coarseAvailable = false;
            try
            {
                _geolocator = new Geolocator();
                _geolocator.DesiredAccuracy = PositionAccuracy.Default;
                _geolocator.DesiredAccuracyInMeters = 3000;
                _geolocator.MovementThreshold = 1000;
                _geolocator.ReportInterval = 600000;

                if (ApiInformation.IsMethodPresent(
                    "Windows.Devices.Geolocation.Geolocator",
                    "AllowFallbackToConsentlessPositions"))
                {
                    _geolocator.AllowFallbackToConsentlessPositions();
                    coarseAvailable = true;
                }

                _geolocator.PositionChanged += OnPositionChanged;
                _geolocator.StatusChanged += OnGeolocatorStatusChanged;
            }
            catch (Exception ex)
            {
                _lastKeepAliveStatus = "Location keep-alive setup failed: " + ex.Message;
                Diag(_lastKeepAliveStatus);
                StopGeolocator();
                return false;
            }

            GeolocationAccessStatus access = GeolocationAccessStatus.Unspecified;
            try
            {
                access = await Geolocator.RequestAccessAsync();
            }
            catch (Exception ex)
            {
                _lastKeepAliveStatus = "Location access request failed: " + ex.Message;
                Diag(_lastKeepAliveStatus);
            }

            Diag("Location access=" + access.ToString() + " coarseFallback=" + coarseAvailable.ToString());

            if (access != GeolocationAccessStatus.Allowed && !coarseAvailable)
            {
                _lastKeepAliveStatus = "Location access is " + access.ToString() + ". Always-on notifications cannot start.";
                Diag(_lastKeepAliveStatus);
                StopGeolocator();
                return false;
            }

            var session = new ExtendedExecutionSession();
            session.Reason = ExtendedExecutionReason.LocationTracking;
            session.Description = "Keeping Telegram notifications alive";
            session.Revoked += OnKeepAliveRevoked;

            try
            {
                var result = await session.RequestExtensionAsync();
                Diag("Location extended execution result=" + result.ToString());
                if (result == ExtendedExecutionResult.Allowed)
                {
                    _keepAliveSession = session;
                    _lastKeepAliveStatus = access == GeolocationAccessStatus.Allowed
                        ? "Always-on background session is active."
                        : "Always-on background session is active with coarse location fallback.";
                    Diag(_lastKeepAliveStatus);
                    Start();
                    return true;
                }

                _lastKeepAliveStatus = "Always-on background session was denied by Windows.";
                Diag(_lastKeepAliveStatus);
            }
            catch (Exception ex)
            {
                _lastKeepAliveStatus = "Always-on background session failed: " + ex.Message;
                Diag(_lastKeepAliveStatus);
            }

            try
            {
                session.Revoked -= OnKeepAliveRevoked;
                session.Dispose();
            }
            catch
            {
            }

            StopGeolocator();
            return false;
        }

        public static void StopKeepAlive()
        {
            Diag("StopKeepAlive. active=" + KeepAliveActive.ToString());
            try
            {
                if (_keepAliveSession != null)
                {
                    _keepAliveSession.Revoked -= OnKeepAliveRevoked;
                    _keepAliveSession.Dispose();
                    _keepAliveSession = null;
                }
            }
            catch
            {
            }

            StopGeolocator();
        }

        private static void StopGeolocator()
        {
            try
            {
                if (_geolocator != null)
                {
                    _geolocator.PositionChanged -= OnPositionChanged;
                    _geolocator.StatusChanged -= OnGeolocatorStatusChanged;
                    _geolocator = null;
                }
            }
            catch
            {
            }
        }

        private static void OnPositionChanged(Geolocator sender, PositionChangedEventArgs args)
        {
            // Location is intentionally unused. The subscription keeps
            // ExtendedExecutionReason.LocationTracking valid on Windows 10 Mobile.
            if (DateTime.UtcNow - _lastPositionDiagnosticUtc >= TimeSpan.FromMinutes(1))
            {
                _lastPositionDiagnosticUtc = DateTime.UtcNow;
                Diag("Location position changed.");
            }
        }

        private static void OnGeolocatorStatusChanged(Geolocator sender, StatusChangedEventArgs args)
        {
            if (args == null) return;
            Diag("Location status changed: " + args.Status.ToString());
            if (args.Status == PositionStatus.Disabled || args.Status == PositionStatus.NotAvailable)
                _lastKeepAliveStatus = "Location status: " + args.Status.ToString();
        }

        private static async void OnKeepAliveRevoked(object sender, ExtendedExecutionRevokedEventArgs args)
        {
            _lastKeepAliveStatus = "Always-on background session revoked: " + (args == null ? string.Empty : args.Reason.ToString()) + Memory();
            Diag(_lastKeepAliveStatus);
            StopKeepAlive();

            if (TelegramAppSettings.NotificationMode == TelegramNotificationMode.Always &&
                args != null &&
                args.Reason == ExtendedExecutionRevokedReason.SystemPolicy)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(1));
                    if (TelegramAppSettings.NotificationMode == TelegramNotificationMode.Always && _keepAliveSession == null)
                        await StartKeepAliveAsync();
                }
                catch
                {
                }
            }
        }
    }
}
