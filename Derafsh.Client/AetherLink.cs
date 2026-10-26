using System.Globalization;
using System.Web;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Specialized;

namespace Derafsh.Client
{
    // ==================== لینک‌های aether:// ====================
    // مثل پترن (PattN): لینک aether://[endpoint]?query#name یک پروفایل WARP (هسته‌ی Aether) رو توصیف می‌کنه.
    // اینجا لینک خونده می‌شه (Parse) و آرگومان‌های اجرای هسته از روی اون ساخته می‌شه (BuildArgs).
    // مقادیر مجاز همون‌هایی هستن که خود هسته (aether 2.3.x) قبول می‌کنه؛ چیز نامعتبر به مقدار پیش‌فرض می‌افته.
    public sealed record AetherLink
    {
        // --- پروتکل (مسیر تونل WARP) ---
        public string Protocol = "wg";          // masque | wg | gool | mim | wg-over-masque
        public string Transport = "h3";         // h3 | h2 (فقط برای مسیرهای MASQUE)
        public string Scan = "balanced";        // turbo | balanced | thorough | verified | ironclad
        public string Noize = "auto";           // auto | off | light | firewall | balanced | gfw | aggressive
        public string IpVersion = "v4";         // v4 | v6 | both

        // --- نشانی ---
        public string? Address;                 // هاست دامنه/آی‌پی (لینک بدون نشانی = اسکن)
        public int Port;                        // 0 یعنی «هیچ»؛ هسته خودش اسکن می‌کنه
        public string? Outer, Inner;            // دو-هاپی‌ها (gool / mim / wg-over-masque)

        // --- MASQUE / TLS ---
        public string? MasqueSni;
        public bool Fragment;
        public string? FragmentSize, FragmentDelay;
        public bool Ech;
        public string? EchDns, EchDomain;
        public string? Fingerprint;             // chrome (پیش‌فرض) | chrome-120 | firefox | semi-python | go

        // --- بقیه ---
        public string? Dns, ExitLoc;
        public string Psiphon = "off";          // off | chain | reverse | only
        public string? PsiphonMode;             // auto | cdn | direct
        public string? PsiphonCdnIps, PsiphonCdnSni, PsiphonCdnSets, PsiphonRegion;
        public bool PsiphonBundledList = true;
        public string Tor = "off";              // off | chain | reverse | only
        public string? TorBridges;              // auto | first | off | own
        public string? TorBridgeLines;          // خط‌های بریج، جدا با ؛
        public string? TorRelays;               // auto | only | off | عدد
        public string? Name;

        // --- خروج (exit-node) از روی کانفیگ سرور: fm و dialMode لینک، به outbound آزادِ خروجی می‌رن ---
        public string? Fm, DialMode;

        public bool OverMasque => Protocol is "masque" or "mim" or "wg-over-masque";
        public bool TwoHops => Protocol is "gool" or "mim" or "wg-over-masque";
        public bool MasqueOverHttp2 => OverMasque && (Transport == "h2" || Tor == "reverse" || Psiphon == "reverse");

        // کلید پایدار برای فایل شناسه‌ی WARP (تا هر بار ثبت‌نام تکراری نشه و محدود نشیم)
        public string IdentityKey
        {
            get
            {
                var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(ToString()));
                var sb = new StringBuilder(16);
                for (int i = 0; i < 8; i++) sb.Append(bytes[i].ToString("x2"));
                return sb.ToString();
            }
        }

        public override string ToString()
        {
            var sb = new StringBuilder("aether://");
            if (!string.IsNullOrEmpty(Address)) sb.Append(Address).Append(Port > 0 ? $":{Port}" : "");
            var q = new List<string>();
            void Add(string k, string? v) { if (!string.IsNullOrEmpty(v)) q.Add($"{k}={Uri.EscapeDataString(v)}"); }
            Add("protocol", Protocol);
            if (TwoHops) { Add("outer", Outer); Add("inner", Inner); }
            if (Transport == "h2") q.Add("transport=h2");
            Add("scan", Scan);
            Add("noize", Noize);
            Add("ip", IpVersion);
            Add("sni", MasqueSni);
            if (Fragment) { q.Add("fragment=1"); Add("fragment_size", FragmentSize); Add("fragment_delay", FragmentDelay); }
            if (Ech) { q.Add("ech=1"); Add("ech_dns", EchDns); Add("ech_domain", EchDomain); }
            Add("fingerprint", Fingerprint);
            Add("dns", Dns);
            Add("exit_loc", ExitLoc);
            if (Psiphon != "off") Add("psiphon", Psiphon);
            Add("psiphon_mode", PsiphonMode);
            Add("cdn_ips", PsiphonCdnIps);
            Add("cdn_sni", PsiphonCdnSni);
            Add("cdn_sets", PsiphonCdnSets);
            Add("region", PsiphonRegion);
            if (!PsiphonBundledList) q.Add("psiphon_bundled=0");
            if (Tor != "off") Add("tor", Tor);
            Add("tor_bridges", TorBridges);
            Add("bridges", TorBridgeLines);
            Add("tor_relays", TorRelays);
            Add("fm", Fm);
            Add("dialMode", DialMode);
            if (q.Count > 0) sb.Append('?').Append(string.Join("&", q));
            if (!string.IsNullOrEmpty(Name)) sb.Append('#').Append(Uri.EscapeDataString(Name));
            return sb.ToString();
        }

        // ==================== پارس لینک ====================
        public static AetherLink? Parse(string? link)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(link)) return null;
                string text = link.Trim();
                if (!text.StartsWith("aether://", StringComparison.OrdinalIgnoreCase)) return null;

                var p = new AetherLink();

                // نام کانفیگ (بعد از #)
                int hash = text.IndexOf('#');
                if (hash >= 0)
                {
                    p.Name = HttpUtility.UrlDecode(text[(hash + 1)..]).Trim();
                    text = text[..hash];
                }

                // endpoint از authority (مثل پترن: نه با Uri دات‌نت، که آی‌پی رو بازنویسی می‌کنه)
                int query = text.IndexOf('?');
                string authority = query < 0 ? text[9..] : text[9..query];
                if (authority.Length > 0)
                {
                    var (host, port) = EndpointOf(authority);
                    p.Address = host;
                    p.Port = port;
                }

                // پارامترها
                var q = query < 0 ? new NameValueCollection() : HttpUtility.ParseQueryString(text[query..]);
                string? V(string k) { var v = q[k]; return string.IsNullOrWhiteSpace(v) ? null : v.Trim(); }

                p.Protocol = OneOf(V("protocol"), "masque", "wg", "gool", "mim", "wg-over-masque") ?? "wg";
                p.Transport = V("transport") == "h2" ? "h2" : "h3";
                p.Scan = OneOf(V("scan"), "turbo", "balanced", "thorough", "verified", "ironclad")
                         ?? (V("scan") == "stealth" ? "verified" : "balanced");
                p.Noize = OneOf(V("noize"), "auto", "off", "light", "firewall", "balanced", "gfw", "aggressive") ?? "auto";
                p.IpVersion = OneOf(V("ip"), "v4", "v6", "both") ?? "v4";

                // دو-هاپی: outer/inner؛ برای gool و mim دو هاپی باید فرق کنن
                if (p.TwoHops)
                {
                    p.Outer = EndpointString(V("outer"));
                    p.Inner = EndpointString(V("inner"));
                    if (p.Inner != null && (p.Protocol == "gool" || p.Protocol == "mim") && SameHost(p.Inner, p.Outer))
                        p.Inner = null;
                }

                if (p.OverMasque) p.MasqueSni = V("sni");
                p.Fragment = q["fragment"] == "1";
                p.FragmentSize = RangeText(V("fragment_size"));
                p.FragmentDelay = RangeText(V("fragment_delay"));
                p.Ech = q["ech"] == "1";
                if (p.Ech) { p.EchDns = V("ech_dns"); p.EchDomain = V("ech_domain"); }
                p.Fingerprint = OneOf(V("fingerprint"), "chrome", "chrome-120", "firefox", "semi-python", "go");
                p.Dns = V("dns");
                p.ExitLoc = V("exit_loc");

                p.Psiphon = OneOf(V("psiphon"), "chain", "reverse", "only") ?? "off";
                p.PsiphonMode = OneOf(V("psiphon_mode"), "auto", "cdn", "direct");
                p.PsiphonCdnIps = V("cdn_ips");
                p.PsiphonCdnSni = V("cdn_sni");
                p.PsiphonCdnSets = V("cdn_sets");
                p.PsiphonRegion = V("region");
                p.PsiphonBundledList = q["psiphon_bundled"] != "0";

                p.Tor = OneOf(V("tor"), "chain", "reverse", "only") ?? "off";
                p.TorBridges = OneOf(V("tor_bridges"), "auto", "first", "off", "own");
                p.TorBridgeLines = V("bridges")?.Replace(';', '\n');
                p.TorRelays = V("tor_relays");

                p.Fm = V("fm");
                p.DialMode = V("dialMode");
                return p;
            }
            catch { return null; }
        }

        // مقادیر مجاز؛ چیز دیگه = null (یعنی پیش‌فرض)
        private static string? OneOf(string? value, params string[] allowed) =>
            value != null && allowed.Contains(value, StringComparer.Ordinal) ? value : null;

        // host:port از authority؛ شبیه پترن فقط آی‌پی/هاست ساده و پورت عددی قبول می‌شه
        private static (string? host, int port) EndpointOf(string authority)
        {
            if (authority.Length == 0) return (null, 0);
            string server = authority[(authority.IndexOf('@') + 1)..];
            if (!server.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':' or '[' or ']'))
                return (null, 0);
            string host; string port;
            if (server.StartsWith('['))
            {
                int close = server.IndexOf(']');
                if (close < 0) return (null, 0);
                host = server[1..close];
                string rest = server[(close + 1)..];
                if (rest.Length > 0 && rest[0] != ':') return (null, 0);
                port = rest.Length > 0 ? rest[1..] : "";
            }
            else
            {
                int colon = server.IndexOf(':');
                host = colon < 0 ? server : server[..colon];
                port = colon < 0 ? "" : server[(colon + 1)..];
            }
            if (!int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n <= 0 || n > 65535)
                return (host.Length > 0 ? host : null, 0);
            return (host, n);
        }

        // ip:port برای هاپی‌ها (مثل 162.159.192.1:2408)؛ پورت الزامیه
        private static string? EndpointString(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var (host, port) = EndpointOf(value.Trim());
            return host != null && port > 0 ? $"{host}:{port}" : null;
        }

        private static bool SameHost(string? a, string? b) =>
            a != null && b != null && a.Split(':')[0] == b.Split(':')[0];

        // «۸-۱۶» یا «۸»: فقط رقم و خط تیره، حداکثر یک خط تیره
        private static string? RangeText(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var t = value.Trim();
            if (t.All(c => char.IsAsciiDigit(c) || c == '-') && t.Count(c => c == '-') <= 1 && t.Any(char.IsAsciiDigit))
                return t;
            return null;
        }

        // ==================== آرگومان‌های اجرای هسته ====================
        // مثل AetherArguments پترن: مشخص می‌شه اپ به کدوم گوش‌دهنده (SOCKS) وصل می‌شه،
        // و بقیه‌ی گوش‌دهنده‌ها (تونل خودش، تور، سایفون) پورت‌های بعدی رو می‌گیرن.
        public sealed class Launch
        {
            public List<string> Args = new();
            public int DialPort;        // پورتی که اپ (xray) بهش وصل می‌شه
            public bool NeedsPsiphonBin;
            public bool NeedsLyrebird;
        }

        /// <summary>آرگومان‌های هسته. port: گوش‌دهنده‌ی اصلی؛ scan: حالت اسکن (تست‌ها)؛ upstreamPort: با «خروج از سرور».</summary>
        public Launch BuildArgs(int port, bool scan, int? upstreamPort = null, string? psiphonBin = null, string? lyrebirdPath = null)
        {
            var dialPsiphon = Psiphon == "chain";
            var dialTor = Tor == "chain" && !dialPsiphon;
            long next = (long)port + 1;
            long own = dialPsiphon || dialTor ? next++ : port;
            long? torBind = null;
            if (Tor == "chain") torBind = dialTor ? port : next++;
            else if (Tor == "reverse") torBind = next++;
            long? psiphonBind = null;
            if (Psiphon == "chain") psiphonBind = dialPsiphon ? port : next++;
            else if (Psiphon == "reverse") psiphonBind = 0;

            var a = new List<string> { "--bind", $"127.0.0.1:{own}" };

            if (Psiphon != "only" && Tor != "only")
            {
                a.AddRange(["--protocol", Protocol == "wg-over-masque" ? "gool" : Protocol]);
                if (Protocol == "gool") a.Add("--gool-classic");
                a.AddRange(["--scan", Scan]);
                if (Noize != "auto" && !MasqueOverHttp2) a.AddRange(["--noize", Noize]);
                a.AddRange(["--ip", IpVersion]);
                if (Clean(Dns) is string dns) a.AddRange(["--dns", dns]);
                // اسکن هم قانون خروجی رو نگه می‌داره تا روی نقطه‌ای تموم شه که نشست قبول می‌کنه
                if (Clean(ExitLoc) is string exitLoc) a.AddRange(["--exit-loc", exitLoc]);

                if (OverMasque)
                    a.AddRange(["--masque-sni", Clean(MasqueSni) ?? "www.cloudflare.com"]);
                if (OverMasque && Transport == "h2")
                {
                    a.Add("--h2");
                    if (Fragment)
                    {
                        a.Add("--fragment");
                        if (FragmentSize != null) a.AddRange(["--fragment-size", FragmentSize]);
                        if (FragmentDelay != null) a.AddRange(["--fragment-delay", FragmentDelay]);
                    }
                }
                if (OverMasque && Ech)
                {
                    a.AddRange(["--ech", "auto"]);
                    a.AddRange(["--ech-dns", Clean(EchDns) ?? "udp://1.1.1.1"]);
                    a.AddRange(["--ech-domain", Clean(EchDomain) ?? "cloudflare-ech.com"]);
                }
                if (OverMasque) a.AddRange(FingerprintArgs(Fingerprint));

                if (Protocol == "wg-over-masque")
                {
                    // هاپی بیرونی: دروازه‌ی MASQUE که اسکن از نو پیداش می‌کنه؛ هاپی درونی: نقطه‌ی WireGuard داخل تونل
                    if (!scan && Outer != null) a.AddRange(["--peer", Outer]);
                    if (Inner != null) a.AddRange(["--gool-peer", Inner]);
                }
                else if (TwoHops)
                {
                    string hop = Protocol == "mim" ? "--mim" : "--wiw";
                    var outer = scan ? null : Outer;
                    var inner = scan ? null : Inner;
                    if (outer != null) a.AddRange([$"{hop}-outer", outer]);
                    if (inner != null) a.AddRange([$"{hop}-inner", inner]);
                    if (outer == null && inner == null) a.Add($"{hop}-scan");
                }
                else if (!scan && Port > 0 && Address != null)
                {
                    a.AddRange(["--peer", $"{Address}:{Port}"]);
                }

                a.Add(scan ? "--no-quick-reconnect" : "--quick-reconnect");
            }

            if (upstreamPort is int up) a.AddRange(["--upstream", $"socks5://127.0.0.1:{up}"]);

            if (Tor == "chain") a.Add("--tor");
            else if (Tor == "reverse") a.Add("--tor-reverse");
            else if (Tor == "only") a.Add("--tor-only");
            if (torBind != null) a.AddRange(["--tor-bind", $"127.0.0.1:{torBind}"]);
            if (Tor != "off")
            {
                if (TorBridges == "first") a.Add("--tor-bridges");
                else if (TorBridges == "off") a.Add("--no-tor-bridges");
                else if (TorBridges == "own" && !string.IsNullOrWhiteSpace(TorBridgeLines))
                {
                    foreach (var line in TorBridgeLines.Split('\n'))
                        if (Clean(line) is string l) a.AddRange(["--tor-bridge", l]);
                }
                if ((TorBridges == "auto" || TorBridges == "first") && TorRelays != null && TorRelays != "auto")
                    a.AddRange(["--tor-relays", TorRelays]);
                if (lyrebirdPath != null) a.AddRange(["--tor-pt", lyrebirdPath]);
            }

            if (Psiphon == "chain") a.Add("--psiphon");
            else if (Psiphon == "reverse") a.Add("--psiphon-reverse");
            else if (Psiphon == "only") a.Add("--psiphon-only");
            if (psiphonBind != null) a.AddRange(["--psiphon-bind", $"127.0.0.1:{psiphonBind}"]);
            if (Psiphon != "off")
            {
                a.AddRange(["--psiphon-mode", PsiphonMode ?? "auto"]);
                string? cdnIps = PsiphonMode != "direct" ? Clean(PsiphonCdnIps) : null;
                if (cdnIps != null)
                {
                    a.AddRange(["--psiphon-cdn-ips", cdnIps]);
                    if (Clean(PsiphonCdnSni) is string sni) a.AddRange(["--psiphon-cdn-sni", sni]);
                }
                if (PsiphonMode != "direct" && Clean(PsiphonCdnSets) is string sets) a.AddRange(["--psiphon-cdn-sets", sets]);
                if (Clean(PsiphonRegion) is string region) a.AddRange(["--psiphon-region", region]);
                if (psiphonBin != null) a.AddRange(["--psiphon-bin", psiphonBin]);
                if (PsiphonBundledList) a.AddRange(["--psiphon-server-entries", "shipped-list"]);
            }

            a.AddRange(["--log-level", "info"]);
            return new Launch
            {
                Args = a,
                DialPort = (int)own,
                NeedsPsiphonBin = Psiphon != "off" && psiphonBin == null,
                NeedsLyrebird = Tor != "off" && lyrebirdPath == null,
            };
        }

        // مقدار تمیز: خالی یا با «-» (که شبیه پرچم هسته می‌شه) قبول نمی‌شه
        private static string? Clean(string? value)
        {
            var t = value?.Trim();
            return string.IsNullOrEmpty(t) || t.StartsWith('-') ? null : t;
        }

        // اثرانگشت‌های ClientHello. هسته‌ی منتشرشده‌ی aether (v2.3.x) از --tls-ciphers و --tls-groups و
        // --disable-grease پشتیبانی می‌کنه (تست‌شده روی باینری رسمی)؛ پس هر اثرانگشتی با همین سه تا نزدیک‌ترین
        // ClientHello ممکن رو می‌سازه. chrome پیش‌فرضِ خود هسته‌ست و چیزی نمی‌فرسته.
        private static List<string> FingerprintArgs(string? name) => (name ?? "chrome") switch
        {
            "go" => Tls(
                "ECDHE-ECDSA-AES128-GCM-SHA256:ECDHE-RSA-AES128-GCM-SHA256:ECDHE-ECDSA-AES256-GCM-SHA384:ECDHE-RSA-AES256-GCM-SHA384:" +
                "ECDHE-ECDSA-CHACHA20-POLY1305:ECDHE-RSA-CHACHA20-POLY1305:ECDHE-ECDSA-AES128-SHA:ECDHE-RSA-AES128-SHA:" +
                "ECDHE-ECDSA-AES256-SHA:ECDHE-RSA-AES256-SHA",
                "X25519MLKEM768:X25519:P-256:P-384:P-521",
                grease: false),
            "firefox" => Tls(
                "ECDHE-ECDSA-AES128-GCM-SHA256:ECDHE-RSA-AES128-GCM-SHA256:ECDHE-ECDSA-CHACHA20-POLY1305:ECDHE-RSA-CHACHA20-POLY1305:" +
                "ECDHE-ECDSA-AES256-GCM-SHA384:ECDHE-RSA-AES256-GCM-SHA384:ECDHE-RSA-AES128-SHA:ECDHE-RSA-AES256-SHA:" +
                "AES128-GCM-SHA256:AES256-GCM-SHA384:AES128-SHA:AES256-SHA",
                "X25519MLKEM768:X25519:P-256:P-384:P-521",
                grease: false),
            "semi-python" => Tls(
                "ECDHE-ECDSA-AES256-GCM-SHA384:ECDHE-RSA-AES256-GCM-SHA384:ECDHE-ECDSA-AES128-GCM-SHA256:ECDHE-RSA-AES128-GCM-SHA256:" +
                "ECDHE-ECDSA-CHACHA20-POLY1305:ECDHE-RSA-CHACHA20-POLY1305:ECDHE-ECDSA-AES256-SHA:ECDHE-RSA-AES256-SHA:" +
                "ECDHE-ECDSA-AES128-SHA256:ECDHE-RSA-AES128-SHA256",
                "X25519MLKEM768:X25519:P-256:P-384:P-521",
                grease: false),
            "chrome-120" => Tls(
                "ALL:!aPSK:!ECDSA+SHA1:!3DES",
                "X25519:P-256:P-384",
                grease: true),
            _ => new List<string>(),   // chrome: پیش‌فرض خود هسته
        };

        private static List<string> Tls(string ciphers, string groups, bool grease)
        {
            var a = new List<string> { "--tls-ciphers", ciphers, "--tls-groups", groups };
            if (!grease) a.Add("--disable-grease");
            return a;
        }
    }
}
