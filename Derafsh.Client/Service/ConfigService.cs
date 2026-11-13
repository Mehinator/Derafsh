using Derafsh.Client.Models;
using System.Text.RegularExpressions;
using System.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System;
using System.Linq;

namespace Derafsh.Client.Services
{
    public class ConfigService
    {
        // --- متد پارس کردن فایل (عمومی) ---
        public List<Server> ParseFile(string path, bool isUser)
        {
            var list = new List<Server>();
            if (!File.Exists(path)) return list;
            try
            {
                var text = File.ReadAllText(path);
                var regex = new Regex(@"(vmess|vless|ss|trojan|hysteria2?)://[a-zA-Z0-9\-\._~:/\?#@!$&'()*+,;=%]+");
                var matches = regex.Matches(text);

                foreach (Match match in matches)
                {
                    string link = match.Value.Trim().TrimEnd(')', ']', '}', '"', '\'', '`');
                    var s = CreateServerObj(link, isUser);
                    if (s != null) list.Add(s);
                }
            }
            catch { }
            return list;
        }

        // --- متد ساخت آبجکت سرور (عمومی) ---
        public Server? CreateServerObj(string link, bool isUser)
        {
            try
            {
                string name = "Server", address = "";
                try
                {
                    if (!link.StartsWith("vmess"))
                    {
                        var uri = new Uri(link);
                        name = HttpUtility.UrlDecode(uri.Fragment.TrimStart('#'));
                        // ss قدیمی کل «روش:رمز@آدرس:پورت» رو base64 می‌کنه؛ اونجا Host آدرس واقعی نیست
                        if (!link.StartsWith("ss://") || link.Split('#')[0].Contains('@')) address = uri.Host;
                        // اسم خالی یا فقط کاراکترهای نامرئی (مثلاً لینکی که با # تموم شده) → host:port
                        if (!HasVisibleText(name)) name = $"{uri.Host}:{uri.Port}";
                    }
                    else
                    {
                        // اسم vmess داخل خود لینک (فیلد ps) هست
                        var v = DecodeVmess(link);
                        name = HasVisibleText(v.ps) ? v.ps! : $"{v.add}:{ToInt(v.port, 443)}";
                        address = v.add ?? "";
                    }
                }
                catch { }

                // استخراج پرچم
                string flag = ExtractFlag(name) ?? "🏳️";
#if WINDOWS
                // ویندوز پرچم ایموجی رو نشون نمی‌ده (دو حرف توی مربع)؛ کد دوحرفی جاش: «🇩🇪 Berlin» → «DE Berlin»
                name = Regex.Replace(name, @"\uD83C[\uDDE6-\uDDFF]\uD83C[\uDDE6-\uDDFF]",
                                     m => new string(new[] { (char)('A' + m.Value[1] - 0xDDE6), (char)('A' + m.Value[3] - 0xDDE6) }));
#endif

                return new Server { Config = link, City = name, Country = flag, Address = address.Trim('[', ']'), IsRemovable = isUser };
            }
            catch { return null; }
        }

        private static bool HasVisibleText(string? s) =>
            s != null && s.Any(c => !char.IsWhiteSpace(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format);

        private string? ExtractFlag(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var match = Regex.Match(text, @"[\uD83C][\uDDE6-\uDDFF][\uD83C][\uDDE6-\uDDFF]");
            return match.Success ? match.Value : null;
        }

        // --- متد تولید کانفیگ Xray (عمومی - حیاتی!) ---
        // کانفیگ با JsonNode ساخته می‌شه، نه با چسباندن رشته: هیچ مقداری از لینک (مسیر، رمز، ...)
        // نمی‌تونه ساختار کانفیگ رو عوض کنه (مثلاً یه inbound اضافه باز کنه)
        // IPهای ایران (از geoip.dat خود xray، فقط IPv4؛ Resources/Raw/iran_ip.txt). صفحه‌ی اصلی یه بار پرش می‌کنه.
        public static IReadOnlyList<string> IranIpRanges { get; set; } = Array.Empty<string>();
        private static readonly string[] PrivateRanges = { "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "127.0.0.0/8", "169.254.0.0/16", "fc00::/7", "fe80::/10" };

        // iranDirect: سایت‌های ایرانی (دامنه‌ی .ir و IPهای ایران) بدون VPN، با اینترنت خود دستگاه. بانک‌ها و درگاه پرداخت
        // (شاپرک) فقط IP ایران رو قبول می‌کنن و این سایت‌ها فیلتر هم نیستن. شبکه‌ی محلی (مودم، چاپگر) همیشه مستقیمه.
        // fragment: تکه‌تکه کردن TLS تا سرور (FragmentOutbound پایین)
        public string GenerateXrayConfig(string link, int localPort = 10809, bool iranDirect = false, bool fragment = false)
        {
            if (string.IsNullOrEmpty(link)) throw new FormatException("Link empty");
            link = link.Trim();
            if (link.Contains('#')) link = link[..link.IndexOf('#')];
            string protocol = link.Contains("://") ? link[..link.IndexOf("://")] : "";

            JsonObject outbound = protocol switch
            {
                "vless" => Vless(link),
                "vmess" => Vmess(link),
                "ss" => Shadowsocks(link),
                "trojan" => Trojan(link),
                "hysteria2" => Hysteria2(link),
                _ => throw new FormatException($"Unsupported protocol: {protocol}"),
            };
            bool useFragment = fragment && FragmentFits(outbound);
            if (useFragment)
            {
                // اتصال تا سرور از راه خروجی «fragment» زده می‌شه؛ keep-alive هم همون‌جا (سوکت مال اونه)
                var stream = outbound["streamSettings"]!.AsObject();
                var sockopt = stream["sockopt"] as JsonObject ?? new JsonObject();
                sockopt.Remove("tcpKeepAliveIdle");
                sockopt.Remove("tcpKeepAliveInterval");
                sockopt["dialerProxy"] = "fragment";
                stream["sockopt"] = sockopt;
            }

            var config = new JsonObject
            {
                ["log"] = new JsonObject { ["loglevel"] = "warning" },
                ["inbounds"] = new JsonArray(new JsonObject
                {
                    ["port"] = localPort,
                    ["listen"] = "127.0.0.1",
                    ["protocol"] = "socks",
                    ["settings"] = new JsonObject { ["auth"] = "noauth", ["udp"] = true },
                    ["sniffing"] = Sniffing(),
                }),
                ["dns"] = Dns(),
                ["outbounds"] = new JsonArray(outbound,
                    new JsonObject { ["protocol"] = "freedom", ["tag"] = "direct" },
                    new JsonObject { ["protocol"] = "blackhole", ["tag"] = "block" },
                    new JsonObject { ["protocol"] = "dns", ["tag"] = "dns-out" }),
                // روتینگ آزاد (AsIs)، با یه استثنا: QUIC (UDP پورت 443) بسته می‌شه.
                // اینستاگرام و یوتیوب اول QUIC امتحان می‌کنن؛ خیلی از کانفیگ‌های رایگان UDP رو بد رد می‌کنن و ویدیو وسطش
                // می‌ایسته تا برنامه دیر به TCP برگرده. با بستنش، برنامه‌ها همون اول از TCP می‌رن (کاری که v2rayNG هم می‌کنه).
                ["routing"] = new JsonObject
                {
                    ["domainStrategy"] = "AsIs",
                    ["rules"] = Rules(iranDirect),
                },
            };
            if (useFragment) config["outbounds"]!.AsArray().Add(FragmentOutbound());
            return config.ToJsonString();
        }

        // تکه‌تکه کردن TLS (مثل MEH و MahsaNG): اولین بسته‌ی دست‌دادن TLS تا سرور (ClientHello، که اسم سایت توشه) به تیکه‌های
        // ۱۴ تا ۳۲ بایتی با ۲ تا ۵ میلی‌ثانیه فاصله فرستاده می‌شه. فیلتر اسم سایت رو نمی‌خونه، اتصال رو تشخیص نمی‌ده و کند یا قطع
        // نمی‌کنه؛ برای هر اتصال تازه حدود ۵۰ میلی‌ثانیه بیشتر. سرور تیکه‌ها رو مثل همیشه سر هم می‌کنه.
        public static string FragmentPackets = "tlshello", FragmentLength = "14-32", FragmentInterval = "2-5";
        public static JsonObject FragmentOutbound() => new()
        {
            ["tag"] = "fragment",
            ["protocol"] = "freedom",
            ["settings"] = new JsonObject { ["fragment"] = new JsonObject { ["packets"] = FragmentPackets, ["length"] = FragmentLength, ["interval"] = FragmentInterval } },
            ["streamSettings"] = new JsonObject { ["sockopt"] = new JsonObject { ["tcpNoDelay"] = true, ["tcpKeepAliveIdle"] = 15, ["tcpKeepAliveInterval"] = 5 } },
        };

        // فقط وقتی TLS یا Reality روی TCP هست (نه hysteria2 و نه h3 که UDP‌ان، نه کانفیگ بدون TLS که ClientHello نداره)
        private static bool FragmentFits(JsonObject outbound)
        {
            if (outbound["streamSettings"] is not JsonObject stream) return false;
            string security = (string?)stream["security"] ?? "", network = (string?)stream["network"] ?? "tcp";
            if (security is not ("tls" or "reality") || network is "hysteria" or "kcp" or "quic") return false;
            return stream["tlsSettings"]?["alpn"] is not JsonArray alpn || alpn.All(a => (string?)a != "h3");
        }

        // سایت‌های ایرانی پرکاربرد که دامنه‌شون .ir نیست (ویندوز اسم سایت رو می‌ده نه IP، پس قانون IP بهشون نمی‌رسه)
        private static readonly string[] IranDomains = { "domain:ir", "domain:digikala.com", "domain:aparat.com", "domain:torob.com", "domain:filimo.com",
                                                         "domain:basalam.com", "domain:sheypoor.com", "domain:digistyle.com", "domain:snapp.taxi" };

        private static JsonObject Sniffing()
        {
            var sniffing = new JsonObject { ["enabled"] = true, ["destOverride"] = new JsonArray("http", "tls") };
#if ANDROID
            // اندروید: تونل فقط IPv4 می‌ده؛ IP اصلی سر جاش می‌مونه (اسم سایت فقط برای انتخاب مسیر خونده می‌شه)، تا قانون
            // IPهای ایران کار کنه و سایت ایرانی مستقیم به همون IP وصل بشه، بدون DNS اضافه
            sniffing["routeOnly"] = true;
#endif
            return sniffing;
        }

        // DNS گوشی (اندروید): هر درخواست DNS که از تونل می‌اومد، یه اتصال تازه به سرور باز می‌کرد (TCP + TLS + ...)، یعنی
        // برای هر اسم سایت تازه نیم تا چند ثانیه معطلی، و روی سرورهایی که UDP رو رد نمی‌کنن اصلاً جواب نمی‌اومد.
        // حالا xray خودش جواب می‌ده: از راه یه اتصال ثابت DoH (روی TCP) از داخل سرور، با حافظه؛ فقط IPv4 چون تونل IPv4 است.
        // (تست روی پورت جدا: قبلاً هر درخواست ۴۵۰ تا ۹۷۰ میلی‌ثانیه، حالا ۱۵۵ میلی‌ثانیه و تکراری‌ها ۱ میلی‌ثانیه)
        private static JsonObject Dns()
        {
#if ANDROID
            return new JsonObject { ["servers"] = new JsonArray("https://1.1.1.1/dns-query", "https://8.8.8.8/dns-query"), ["queryStrategy"] = "UseIPv4" };
#else
            return new JsonObject { ["servers"] = new JsonArray("8.8.8.8", "1.1.1.1") };
#endif
        }

        private static JsonArray Rules(bool iranDirect)
        {
            var rules = new JsonArray();
            rules.Add(new JsonObject { ["type"] = "field", ["network"] = "udp", ["port"] = "53", ["outboundTag"] = "dns-out" });
            if (iranDirect)
                rules.Add(new JsonObject { ["type"] = "field", ["domain"] = new JsonArray(IranDomains.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()), ["outboundTag"] = "direct" });
            var directIps = iranDirect ? PrivateRanges.Concat(IranIpRanges) : PrivateRanges;
            rules.Add(new JsonObject { ["type"] = "field", ["ip"] = new JsonArray(directIps.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()), ["outboundTag"] = "direct" });
            rules.Add(new JsonObject { ["type"] = "field", ["network"] = "udp", ["port"] = "443", ["outboundTag"] = "block" });
            rules.Add(new JsonObject { ["type"] = "field", ["outboundTag"] = "proxy", ["port"] = "0-65535" });
            return rules;
        }

        // اندروید: روی گوشی‌های ۳۲ بیتی xray نسخه‌ی لینوکسیه و DNS گوشی رو پیدا نمی‌کنه، پس آدرس سرور اینجا با DNS
        // خود اندروید به IP تبدیل می‌شه (روی همه‌ی گوشی‌ها، تا رفتار یکی باشه). اسم TLS (serverName) و Host موقع
        // ساختن کانفیگ از روی دامنه پر شدن و عوض نمی‌شن. همه‌ی outboundها (از جمله خروجِ زنجیره‌ی ایتر) حل می‌شن.
        public static async Task<string> ResolveServerAddressAsync(string configJson)
        {
            var root = JsonNode.Parse(configJson)!;
            foreach (var outboundNode in root["outbounds"]!.AsArray())
            {
                var outbound = outboundNode!.AsObject();
                if ((string?)outbound["protocol"] == "freedom") continue;
                var settings = outbound["settings"]?.AsObject();
                if (settings == null) continue;
                // vless/vmess: vnext؛ trojan/ss/socks: servers؛ hysteria2: خود settings
                JsonNode? target = settings["vnext"]?[0] ?? settings["servers"]?[0] ?? (settings.ContainsKey("address") ? settings : null);
                string? address = (string?)target?["address"];
                if (string.IsNullOrEmpty(address) || System.Net.IPAddress.TryParse(address, out _)) continue;

                var ips = await System.Net.Dns.GetHostAddressesAsync(address);
                var ip = ips.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? ips.FirstOrDefault()
                         ?? throw new Exception($"Couldn't find the address of {address}.");
                target!["address"] = ip.ToString();
            }
            return root.ToJsonString();
        }

        private static JsonObject Vless(string link)
        {
            var uri = new Uri(link);
            var q = HttpUtility.ParseQueryString(uri.Query);
            var user = new JsonObject { ["id"] = Uri.UnescapeDataString(uri.UserInfo), ["encryption"] = q["encryption"] ?? "none", ["level"] = 0 };
            if (!string.IsNullOrEmpty(q["flow"])) user["flow"] = q["flow"];
            return new JsonObject
            {
                ["tag"] = "proxy",
                ["protocol"] = "vless",
                ["settings"] = new JsonObject { ["vnext"] = new JsonArray(new JsonObject { ["address"] = uri.Host, ["port"] = PortOf(uri), ["users"] = new JsonArray(user) }) },
                ["streamSettings"] = Stream(q, uri.Host, defaultSecurity: "none", network: q["type"]),
            };
        }

        private static JsonObject Vmess(string link)
        {
            var v = DecodeVmess(link);
            string address = v.add ?? throw new FormatException("vmess link without address");
            string host = string.IsNullOrEmpty(v.host) ? address : v.host;
            string net = string.IsNullOrEmpty(v.net) ? "tcp" : v.net;
            string path = string.IsNullOrEmpty(v.path) ? "/" : v.path;

            // در vmess فیلد type همون headerType هست، و برای grpc مسیر = serviceName
            var stream = Transport(net, host, path, v.type, serviceName: v.path, mode: null);
            Secure(stream, v.tls == "tls" ? "tls" : "none", string.IsNullOrEmpty(v.sni) ? host : v.sni, v.fp, null, null, null,
                cs: v.cs, alpn: v.alpn, network: net);
            return new JsonObject
            {
                ["tag"] = "proxy",
                ["protocol"] = "vmess",
                ["settings"] = new JsonObject
                {
                    ["vnext"] = new JsonArray(new JsonObject
                    {
                        ["address"] = address,
                        ["port"] = ToInt(v.port, 443),
                        ["users"] = new JsonArray(new JsonObject { ["id"] = v.id ?? "", ["alterId"] = 0, ["security"] = string.IsNullOrEmpty(v.scy) ? "auto" : v.scy }),
                    }),
                },
                ["streamSettings"] = stream,
            };
        }

        private static JsonObject Shadowsocks(string link)
        {
            var uri = new Uri(link);
            string userInfo = Uri.UnescapeDataString(uri.UserInfo);
            string method, password;
            // SIP002: base64(method:password)؛ بعضی لینک‌ها method:password رو بدون base64 دارن
            try { (method, password) = SplitOnce(System.Text.Encoding.UTF8.GetString(FromBase64Loose(userInfo))); }
            catch (FormatException) { (method, password) = SplitOnce(userInfo); }
            return new JsonObject
            {
                ["tag"] = "proxy",
                ["protocol"] = "shadowsocks",
                ["settings"] = new JsonObject { ["servers"] = new JsonArray(new JsonObject { ["address"] = uri.Host, ["port"] = PortOf(uri), ["method"] = method, ["password"] = password }) },
            };
        }

        private static JsonObject Trojan(string link)
        {
            var uri = new Uri(link);
            var q = HttpUtility.ParseQueryString(uri.Query);
            return new JsonObject
            {
                ["tag"] = "proxy",
                ["protocol"] = "trojan",
                ["settings"] = new JsonObject { ["servers"] = new JsonArray(new JsonObject { ["address"] = uri.Host, ["port"] = PortOf(uri), ["password"] = Uri.UnescapeDataString(uri.UserInfo) }) },
                ["streamSettings"] = Stream(q, uri.Host, defaultSecurity: "tls", network: q["type"]),
            };
        }

        // Hysteria2 از Xray 26 به بعد: پروتکل و انتقال "hysteria" با version 2؛ obfs (salamander) در finalmask
        private static JsonObject Hysteria2(string link)
        {
            var uri = new Uri(link);
            var q = HttpUtility.ParseQueryString(uri.Query);
            var tls = new JsonObject { ["serverName"] = q["sni"] ?? uri.Host, ["alpn"] = new JsonArray("h3") };
            string? pin = q["pcs"] ?? q["pinSHA256"];
            if (!string.IsNullOrEmpty(pin)) tls["pinnedPeerCertSha256"] = pin;
            var stream = new JsonObject
            {
                ["network"] = "hysteria",
                ["hysteriaSettings"] = new JsonObject { ["version"] = 2, ["auth"] = Uri.UnescapeDataString(uri.UserInfo) },
                ["security"] = "tls",
                ["tlsSettings"] = tls,
            };
            if (q["obfs"] == "salamander")
                stream["finalmask"] = new JsonObject
                {
                    ["udp"] = new JsonArray(new JsonObject { ["type"] = "salamander", ["settings"] = new JsonObject { ["password"] = q["obfs-password"] ?? "" } }),
                };
            return new JsonObject
            {
                ["tag"] = "proxy",
                ["protocol"] = "hysteria",
                ["settings"] = new JsonObject { ["version"] = 2, ["address"] = uri.Host, ["port"] = PortOf(uri) },
                ["streamSettings"] = stream,
            };
        }

        // انتقال (tcp, ws, grpc, xhttp, ...) و امنیت (tls / reality) از پارامترهای لینک
        // مثل پترن: cs (cipherSuites) و alpn هم خونده می‌شن — بدون اون‌ها DPI ایران ClientHello رو می‌بنده
        // و کانفیگ سالم «مرده» نشون داده می‌شه.
        private static JsonObject Stream(NameValueCollection q, string address, string defaultSecurity, string? network = null)
        {
            string host = q["host"] ?? q["sni"] ?? address;
            var stream = Transport(q["type"] ?? "tcp", host, q["path"] ?? "/", q["headerType"], q["serviceName"], q["mode"]);
            Secure(stream, q["security"] ?? defaultSecurity, q["sni"] ?? host, q["fp"], q["pbk"], q["sid"], q["spx"], q["pcs"], q["vcn"],
                cs: q["cs"], alpn: q["alpn"], network: network);
            return stream;
        }

        private static JsonObject Transport(string network, string host, string path, string? headerType, string? serviceName, string? mode)
        {
            if (network == "splithttp") network = "xhttp";   // اسم قدیمی xhttp
            var s = new JsonObject { ["network"] = network };
            switch (network)
            {
                case "ws":
                    s["wsSettings"] = new JsonObject { ["path"] = path, ["host"] = host };
                    break;
                case "grpc":
                    s["grpcSettings"] = new JsonObject { ["serviceName"] = serviceName ?? "", ["multiMode"] = mode == "multi" };
                    break;
                case "xhttp":
                    s["xhttpSettings"] = new JsonObject { ["path"] = path, ["host"] = host, ["mode"] = string.IsNullOrEmpty(mode) ? "auto" : mode };
                    break;
                case "httpupgrade":
                    s["httpupgradeSettings"] = new JsonObject { ["path"] = path, ["host"] = host };
                    break;
                case "http":
                case "h2":
                    s["network"] = "http";
                    s["httpSettings"] = new JsonObject { ["path"] = path, ["host"] = new JsonArray(host), ["method"] = "GET" };
                    break;
                case "tcp":
                case "raw":
                    if (headerType == "http")
                    {
                        var request = new JsonObject
                        {
                            ["version"] = "1.1",
                            ["method"] = "GET",
                            ["path"] = new JsonArray(path),
                            ["headers"] = new JsonObject { ["Host"] = new JsonArray(host), ["User-Agent"] = new JsonArray("Mozilla/5.0") },
                        };
                        s["tcpSettings"] = new JsonObject { ["header"] = new JsonObject { ["type"] = "http", ["request"] = request } };
                    }
                    else if (!string.IsNullOrEmpty(headerType) && headerType != "none")
                        s["tcpSettings"] = new JsonObject { ["header"] = new JsonObject { ["type"] = headerType } };
                    break;
            }
            // keep-alive: اتصالی که وسط راه بی‌صدا مرده (مثلاً ویدیو وسط پخش ایستاده) زودتر بسته می‌شه تا برنامه دوباره وصل
            // بشه؛ پیش‌فرض Go تا حدود دو و نیم دقیقه صبر می‌کرد (۱۵ ثانیه + ۹ × ۱۵ ثانیه)
            // TCP Fast Open خاموش (مثل بقیه‌ی برنامه‌ها): سرورهای رایگان پشتیبانی‌اش نمی‌کنن و بسته‌ی SYN با داده رو بعضی
            // فیلترها دور می‌ریزن؛ اون‌وقت هر اتصال تازه یه ثانیه منتظر تکرار SYN می‌مونه
            s["sockopt"] = new JsonObject { ["tcpKeepAliveIdle"] = 15, ["tcpKeepAliveInterval"] = 5 };
            return s;
        }

        // Xray 26 دیگه allowInsecure نداره: گواهی سرور همیشه بررسی می‌شه، مگه اینکه لینک اثرانگشت گواهی (pcs) یا اسم مجاز (vcn) داده باشه
        //
        // cs (cipherSuites) و alpn (مثل پترن):
        //   • cs فقط با fp=unsafe اعمال می‌شه (فورک پترن با ClientHello سازنده‌ی uTLS لیست سیفر رو نمی‌فرسته؛
        //     اونجا cs عملاً بی‌اثره) — ما هر دو مقدار رو عیناً پاس می‌دیم، انتخاب با لینک سازنده‌ست.
        //   • ws/httpupgrade بعد از دست‌دادن فقط HTTP/1.1 حرف می‌زنن؛ اگه لینک alpn دیگه‌ای بده (مثلاً h2,http/1.1)
        //     کلادفلر h2 رو برمی‌داره و اتصال می‌شکنه → مثل FixImportedAlpn پترن، به http/1.1 اصلاح می‌شه.
        private static void Secure(JsonObject s, string security, string serverName, string? fp, string? pbk, string? sid, string? spx, string? pcs = null, string? vcn = null,
            string? cs = null, string? alpn = null, string? network = null)
        {
            s["security"] = security;
            string fingerprint = string.IsNullOrEmpty(fp) ? "chrome" : fp;
            bool isUpgrade = network is "ws" or "httpupgrade";
            if (security == "tls")
            {
                var tls = new JsonObject { ["serverName"] = serverName, ["fingerprint"] = fingerprint };
                if (!string.IsNullOrEmpty(cs)) tls["cipherSuites"] = cs;
                if (FixedAlpn(alpn, isUpgrade) is string a) tls["alpn"] = new JsonArray(a.Split(',').Where(x => x.Length > 0).Select(x => JsonValue.Create(x)!).ToArray());
                if (!string.IsNullOrEmpty(pcs)) tls["pinnedPeerCertSha256"] = pcs;
                if (!string.IsNullOrEmpty(vcn)) tls["verifyPeerCertByName"] = vcn;
                s["tlsSettings"] = tls;
            }
            else if (security == "reality")
                s["realitySettings"] = new JsonObject { ["show"] = false, ["fingerprint"] = fingerprint, ["serverName"] = serverName, ["publicKey"] = pbk ?? "", ["shortId"] = sid ?? "", ["spiderX"] = spx ?? "" };
        }

        // alpn لینک، با اصلاح ws/httpupgrade مثل پترن: هر چیز دیگه‌ای جز http/1.1 → http/1.1
        private static string? FixedAlpn(string? alpn, bool isUpgrade)
        {
            if (string.IsNullOrWhiteSpace(alpn)) return null;
            var names = alpn.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            if (isUpgrade && (names.Count != 1 || names[0] != "http/1.1")) return "http/1.1";
            return names.Count > 0 ? string.Join(",", names) : null;
        }

        private static ConfigDto DecodeVmess(string link)
        {
            string body = link["vmess://".Length..];
            if (body.Contains('#')) body = body[..body.IndexOf('#')];
            string json = System.Text.Encoding.UTF8.GetString(FromBase64Loose(body)).TrimStart('﻿');
            return JsonSerializer.Deserialize<ConfigDto>(json) ?? throw new FormatException("Invalid vmess link");
        }

        // base64 معمولی یا URL-safe، با یا بدون = آخرش
        private static byte[] FromBase64Loose(string s)
        {
            s = s.Trim().Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
        }

        private static (string, string) SplitOnce(string s)
        {
            int i = s.IndexOf(':');
            return i < 0 ? (s, "") : (s[..i], s[(i + 1)..]);
        }

        private static int PortOf(Uri uri) => uri.Port == -1 ? 443 : uri.Port;
        private static int ToInt(object? value, int fallback) => int.TryParse(value?.ToString(), out var n) ? n : fallback;
    }
}
