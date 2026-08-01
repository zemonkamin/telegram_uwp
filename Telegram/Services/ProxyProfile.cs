using System;
using System.Collections.Generic;
using System.ComponentModel;
using Newtonsoft.Json;
using Windows.Storage;

namespace Telegram.Services
{
    // A single saved proxy. The login screen keeps an ordered list of these and
    // marks one as active (IsActive) when the app is connected through it.
    public sealed class ProxyProfile : INotifyPropertyChanged
    {
        public string Id { get; set; }
        public string Mode { get; set; }
        public string Server { get; set; }
        public string Port { get; set; }
        public string Secret { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }

        private bool _isActive;

        [JsonIgnore]
        public bool IsActive
        {
            get { return _isActive; }
            set
            {
                if (_isActive != value)
                {
                    _isActive = value;
                    Raise("IsActive");
                }
            }
        }

        [JsonIgnore]
        public string Title
        {
            get
            {
                var server = string.IsNullOrEmpty(Server) ? "proxy" : Server;
                return string.IsNullOrEmpty(Port) ? server : server + ":" + Port;
            }
        }

        [JsonIgnore]
        public string Subtitle
        {
            get { return ModeLabel(Mode); }
        }

        [JsonIgnore]
        public string Initial
        {
            get
            {
                var m = ModeLabel(Mode);
                return string.IsNullOrEmpty(m) ? "P" : m.Substring(0, 1);
            }
        }

        public void RaiseContentChanged()
        {
            Raise("Title");
            Raise("Subtitle");
            Raise("Initial");
        }

        public ProxySettings ToSettings()
        {
            return new ProxySettings
            {
                Mode = ProxySettings.NormalizeMode(Mode),
                Server = Server ?? "",
                Port = Port ?? "",
                Secret = Secret ?? "",
                Username = Username ?? "",
                Password = Password ?? ""
            };
        }

        public static string ModeLabel(string mode)
        {
            if (string.Equals(mode, ProxySettings.ModeMtproto, StringComparison.OrdinalIgnoreCase))
                return "MTProto";
            if (string.Equals(mode, ProxySettings.ModeHttp, StringComparison.OrdinalIgnoreCase))
                return "HTTP";
            if (string.Equals(mode, ProxySettings.ModeSocks, StringComparison.OrdinalIgnoreCase))
                return "SOCKS5";
            return "Proxy";
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void Raise(string name)
        {
            var handler = PropertyChanged;
            if (handler != null)
                handler(this, new PropertyChangedEventArgs(name));
        }
    }

    public static class ProxyStore
    {
        private const string KeyList = "proxy_profiles_v1";
        private const string KeyEnabled = "proxy_enabled_v1";
        private const string KeySelected = "proxy_selected_v1";

        public static bool Enabled
        {
            get
            {
                object value;
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(KeyEnabled, out value) && value is bool)
                    return (bool)value;
                return false;
            }
            set { ApplicationData.Current.LocalSettings.Values[KeyEnabled] = value; }
        }

        public static string SelectedId
        {
            get
            {
                object value;
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(KeySelected, out value) && value != null)
                    return value.ToString();
                return "";
            }
            set { ApplicationData.Current.LocalSettings.Values[KeySelected] = value ?? ""; }
        }

        public static List<ProxyProfile> LoadProfiles()
        {
            List<ProxyProfile> result = null;
            try
            {
                object value;
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(KeyList, out value) && value != null)
                {
                    var json = value.ToString();
                    if (!string.IsNullOrEmpty(json))
                        result = JsonConvert.DeserializeObject<List<ProxyProfile>>(json);
                }
            }
            catch
            {
            }

            if (result == null)
                result = new List<ProxyProfile>();

            // One-time migration from the old single-proxy settings.
            if (result.Count == 0)
            {
                var legacy = ProxySettings.Load();
                if (legacy != null && !ProxySettings.IsSystemMode(legacy.Mode) && !string.IsNullOrWhiteSpace(legacy.Server))
                {
                    result.Add(new ProxyProfile
                    {
                        Id = NewId(),
                        Mode = ProxySettings.NormalizeMode(legacy.Mode),
                        Server = legacy.Server,
                        Port = legacy.Port,
                        Secret = legacy.Secret,
                        Username = legacy.Username,
                        Password = legacy.Password
                    });
                    SaveProfiles(result);
                }
            }

            for (var i = 0; i < result.Count; i++)
            {
                if (string.IsNullOrEmpty(result[i].Id))
                    result[i].Id = NewId();
            }

            return result;
        }

        public static void SaveProfiles(IEnumerable<ProxyProfile> profiles)
        {
            try
            {
                var json = JsonConvert.SerializeObject(profiles);
                ApplicationData.Current.LocalSettings.Values[KeyList] = json;
            }
            catch
            {
            }
        }

        public static string NewId()
        {
            return Guid.NewGuid().ToString("N");
        }
    }
}
