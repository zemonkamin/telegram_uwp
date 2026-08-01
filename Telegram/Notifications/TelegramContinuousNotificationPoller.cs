using System;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Services;
using Windows.Devices.Geolocation;
using Windows.ApplicationModel.ExtendedExecution;
using Windows.Foundation.Metadata;
using Windows.Storage;

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

            Diag("Poller started. mode=" + TelegramAppSettings.NotificationMode.ToString() + " keepAlive=" + KeepAliveActive.ToString());
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

            Diag("EnterBackground: mode=" + TelegramAppSettings.NotificationMode.ToString());
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
                            Diag("PollAndNotify tick. mode=" + mode.ToString() + " keepAlive=" + KeepAliveActive.ToString());
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

        public static async Task<bool> StartKeepAliveAsync()
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
            _lastKeepAliveStatus = "Always-on background session revoked: " + (args == null ? string.Empty : args.Reason.ToString());
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
