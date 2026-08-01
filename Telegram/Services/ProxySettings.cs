using System;
using Windows.Foundation.Collections;
using Windows.Storage;

namespace Telegram.Services
{
    public sealed class ProxySettings
    {
        public const string ModeSystem = "system";
        public const string ModeNone = "none";
        public const string ModeMtproto = "mtproto";
        public const string ModeHttp = "http";
        public const string ModeSocks = "socks5";

        public string Mode { get; set; }
        public string Server { get; set; }
        public string Port { get; set; }
        public string Secret { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }

        public ProxySettings()
        {
            Mode = ModeSystem;
            Server = "";
            Port = "";
            Secret = "";
            Username = "";
            Password = "";
        }

        public static ProxySettings Load()
        {
            var values = ApplicationData.Current.LocalSettings.Values;
            return new ProxySettings
            {
                Mode = NormalizeMode(Read(values, "td_proxy_mode", Read(values, "proxy_mode", ModeSystem))),
                Server = Read(values, "td_proxy_server", Read(values, "proxy_server", "")),
                Port = Read(values, "td_proxy_port", Read(values, "proxy_port", "")),
                Secret = Read(values, "td_proxy_secret", Read(values, "proxy_secret", "")),
                Username = Read(values, "td_proxy_username", Read(values, "proxy_username", "")),
                Password = Read(values, "td_proxy_password", Read(values, "proxy_password", ""))
            };
        }

        public void Save()
        {
            var values = ApplicationData.Current.LocalSettings.Values;
            var mode = NormalizeMode(Mode);
            values["td_proxy_mode"] = mode;
            values["td_proxy_server"] = Server ?? "";
            values["td_proxy_port"] = Port ?? "";
            values["td_proxy_secret"] = Secret ?? "";
            values["td_proxy_username"] = Username ?? "";
            values["td_proxy_password"] = Password ?? "";
            values["proxy_mode"] = mode;
            values["proxy_server"] = Server ?? "";
            values["proxy_port"] = Port ?? "";
            values["proxy_secret"] = Secret ?? "";
            values["proxy_username"] = Username ?? "";
            values["proxy_password"] = Password ?? "";
        }

        public static void Clear()
        {
            new ProxySettings().Save();
        }

        public static bool TryParseProxyLink(string link, out ProxySettings settings)
        {
            settings = null;
            if (string.IsNullOrWhiteSpace(link)) return false;

            Uri uri;
            if (!Uri.TryCreate(link.Trim(), UriKind.Absolute, out uri)) return false;

            var scheme = (uri.Scheme ?? "").ToLowerInvariant();
            var host = uri.Host;
            var port = uri.Port > 0 ? uri.Port.ToString() : "";
            var query = ParseQuery(uri.Query);

            if (scheme == "tg" && uri.Host == "proxy")
            {
                host = Get(query, "server");
                port = Get(query, "port");
                settings = new ProxySettings
                {
                    Mode = ModeMtproto,
                    Server = host,
                    Port = port,
                    Secret = Get(query, "secret")
                };
                return !string.IsNullOrWhiteSpace(settings.Server) && !string.IsNullOrWhiteSpace(settings.Port);
            }

            if ((scheme == "https" || scheme == "http") &&
                uri.Host.EndsWith("t.me", StringComparison.OrdinalIgnoreCase) &&
                uri.AbsolutePath.Trim('/').Equals("proxy", StringComparison.OrdinalIgnoreCase))
            {
                settings = new ProxySettings
                {
                    Mode = ModeMtproto,
                    Server = Get(query, "server"),
                    Port = Get(query, "port"),
                    Secret = Get(query, "secret")
                };
                return !string.IsNullOrWhiteSpace(settings.Server) && !string.IsNullOrWhiteSpace(settings.Port);
            }

            return false;
        }

        public static bool IsSystemMode(string mode)
        {
            return string.IsNullOrWhiteSpace(mode) ||
                string.Equals(mode, ModeSystem, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode, ModeNone, StringComparison.OrdinalIgnoreCase);
        }

        public static string NormalizeMode(string mode)
        {
            if (IsSystemMode(mode)) return ModeSystem;
            if (string.Equals(mode, ModeMtproto, StringComparison.OrdinalIgnoreCase)) return ModeMtproto;
            if (string.Equals(mode, ModeHttp, StringComparison.OrdinalIgnoreCase)) return ModeHttp;
            if (string.Equals(mode, ModeSocks, StringComparison.OrdinalIgnoreCase)) return ModeSocks;
            return ModeSystem;
        }

        private static IPropertySet ParseQuery(string query)
        {
            var map = new ValueSet();
            if (string.IsNullOrEmpty(query)) return map;
            var text = query[0] == '?' ? query.Substring(1) : query;
            var parts = text.Split('&');
            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                var eq = part.IndexOf('=');
                if (eq <= 0) continue;
                var key = Uri.UnescapeDataString(part.Substring(0, eq));
                var value = Uri.UnescapeDataString(part.Substring(eq + 1));
                map[key] = value;
            }
            return map;
        }

        private static string Get(IPropertySet values, string key)
        {
            return Read(values, key, "");
        }

        private static string Read(IPropertySet values, string key, string fallback)
        {
            object value;
            if (values != null && values.TryGetValue(key, out value) && value != null)
                return value.ToString();
            return fallback;
        }
    }
}
