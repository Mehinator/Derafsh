using Derafsh.Client.Models;
using System.Text.RegularExpressions;
using System.Web;
using System.Text.Json;
using System.Collections.Generic;
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
                string name = "Server";
                try
                {
                    if (!link.StartsWith("vmess"))
                    {
                        var uri = new Uri(link);
                        name = !string.IsNullOrEmpty(uri.Fragment) ? HttpUtility.UrlDecode(uri.Fragment.TrimStart('#')) : $"{uri.Host}:{uri.Port}";
                    }
                    else name = "Vmess Server";
                }
                catch { }

                // استخراج پرچم
                string flag = ExtractFlag(name) ?? "🏳️";

                return new Server { Config = link, City = name, Country = flag, IsRemovable = isUser };
            }
            catch { return null; }
        }

        private string? ExtractFlag(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var match = Regex.Match(text, @"[\uD83C][\uDDE6-\uDDFF][\uD83C][\uDDE6-\uDDFF]");
            return match.Success ? match.Value : null;
        }

        // --- متد تولید کانفیگ Xray (عمومی - حیاتی!) ---
        // *** اینجا کلمه public اضافه شد ***
        public string GenerateXrayConfig(string link)
        {
            if (string.IsNullOrEmpty(link)) throw new FormatException("Link empty");
            link = link.Trim();
            if (link.Contains("#")) link = link.Substring(0, link.IndexOf('#'));

            // هدر استاندارد
            string header = @"""log"":{""loglevel"":""warning""},""inbounds"":[{""port"":10809,""listen"":""127.0.0.1"",""protocol"":""socks"",""settings"":{""auth"":""noauth"",""udp"":true},""sniffing"":{""enabled"":true,""destOverride"":[""http"",""tls""]}}],""dns"":{""servers"":[""8.8.8.8"",""1.1.1.1""]},";

            // روتینگ آزاد (AsIs)
            string routing = @"""routing"":{""domainStrategy"":""AsIs"",""rules"":[{""type"":""field"",""outboundTag"":""proxy"",""port"":""0-65535""}]}";

            string outboundObj = "";
            string protocol = link.Contains("://") ? link.Substring(0, link.IndexOf("://")) : "";

            try
            {
                if (protocol == "vless")
                {
                    var uri = new Uri(link);
                    var q = HttpUtility.ParseQueryString(uri.Query);
                    string encryption = q["encryption"] ?? "none";
                    string flow = q["flow"] ?? "";
                    string type = q["type"] ?? "tcp";
                    string security = q["security"] ?? "none";
                    string headerType = q["headerType"] ?? "none";
                    string path = q["path"] ?? "/";
                    string host = q["host"] ?? q["sni"] ?? uri.Host;

                    string netSettings = "";
                    if (type == "ws") netSettings = $@"""wsSettings"":{{""path"":""{path}"",""headers"":{{""Host"":""{host}""}}}},";
                    else if (type == "grpc") netSettings = $@"""grpcSettings"":{{""serviceName"":""{q["serviceName"]}""}},";
                    else if (type == "http" || type == "xhttp") netSettings = $@"""httpSettings"":{{""path"":""{path}"",""host"":[""{host}""],""method"":""GET""}},";
                    else if (type == "tcp")
                    {
                        if (headerType == "http") netSettings = $@"""tcpSettings"":{{""header"":{{""type"":""http"",""request"":{{""version"":""1.1"",""method"":""GET"",""path"":[""{path}""],""headers"":{{""Host"":[""{host}""],""User-Agent"":[""Mozilla/5.0""]}}}}}},";
                        else if (headerType != "none") netSettings = $@"""tcpSettings"":{{""header"":{{""type"":""{headerType}""}}}},";
                    }

                    string secSettings = "";
                    if (security == "reality") secSettings = $@"""realitySettings"":{{""show"":false,""fingerprint"":""{q["fp"] ?? "chrome"}"",""serverName"":""{q["sni"] ?? host}"",""publicKey"":""{q["pbk"]}"",""shortId"":""{q["sid"]}"",""spiderX"":""""}},";
                    else if (security == "tls") secSettings = $@"""tlsSettings"":{{""serverName"":""{q["sni"] ?? host}"",""fingerprint"":""{q["fp"] ?? "chrome"}"",""allowInsecure"":true}},";

                    string flowJson = !string.IsNullOrEmpty(flow) ? $@"""flow"":""{flow}""," : "";
                    outboundObj = $@"{{""tag"":""proxy"",""protocol"":""vless"",""settings"":{{""vnext"":[{{""address"":""{uri.Host}"",""port"":{uri.Port},""users"":[{{""id"":""{uri.UserInfo}"",""encryption"":""{encryption}"",{flowJson}""level"":0}}]}}]}},""streamSettings"":{{""network"":""{type}"",""security"":""{security}"",{secSettings}{netSettings}""sockopt"":{{""tcpFastOpen"":true}}}}}}";
                }
                else if (protocol == "vmess")
                {
                    string base64 = link.Substring(8);
                    string decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));
                    var v = JsonSerializer.Deserialize<ConfigDto>(decoded);
                    string port = v.port?.ToString() ?? "443";
                    string netSettings = v.net == "ws" ? $@"""wsSettings"":{{""path"":""{v.path}"",""headers"":{{""Host"":""{v.host}""}}}}," : "";
                    string tlsSettings = v.tls == "tls" ? $@"""security"":""tls"",""tlsSettings"":{{""serverName"":""{v.sni ?? v.host}"",""allowInsecure"":true,""fingerprint"":""{v.fp ?? "chrome"}""}}," : $@"""security"":""none"",";
                    outboundObj = $@"{{""tag"":""proxy"",""protocol"":""vmess"",""settings"":{{""vnext"":[{{""address"":""{v.add}"",""port"":{port},""users"":[{{""id"":""{v.id}"",""alterId"":0,""security"":""auto""}}]}}]}},""streamSettings"":{{""network"":""{v.net}"",{tlsSettings}{netSettings}""sockopt"":{{""tcpFastOpen"":true}}}}}}";
                }
                else if (protocol == "ss")
                {
                    link = HttpUtility.UrlDecode(link);
                    var uri = new Uri(link);
                    string userInfo = uri.UserInfo;
                    if (string.IsNullOrEmpty(userInfo)) userInfo = uri.OriginalString.Replace("ss://", "").Split('@')[0];
                    string base64 = userInfo.Split('#')[0].Trim().Replace(" ", "+").Replace("-", "+").Replace("_", "/");
                    while (base64.Length % 4 != 0) base64 += "=";
                    string decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));
                    var parts = decoded.Split(':');
                    outboundObj = $@"{{""tag"":""proxy"",""protocol"":""shadowsocks"",""settings"":{{""servers"":[{{""address"":""{uri.Host}"",""port"":{uri.Port},""method"":""{parts[0]}"",""password"":""{(parts.Length > 1 ? parts[1] : "")}""}}]}}}}";
                }
                else if (protocol == "trojan")
                {
                    var uri = new Uri(link);
                    var q = HttpUtility.ParseQueryString(uri.Query);
                    string sni = q["sni"] ?? uri.Host;
                    outboundObj = $@"{{""tag"":""proxy"",""protocol"":""trojan"",""settings"":{{""servers"":[{{""address"":""{uri.Host}"",""port"":{uri.Port},""password"":""{uri.UserInfo}""}}]}},""streamSettings"":{{""network"":""tcp"",""security"":""tls"",""tlsSettings"":{{""serverName"":""{sni}"",""allowInsecure"":true}},""sockopt"":{{""tcpFastOpen"":true}}}}}}";
                }
                else if (protocol == "hysteria2")
                {
                    var uri = new Uri(link);
                    var q = HttpUtility.ParseQueryString(uri.Query);
                    string sni = q["sni"] ?? uri.Host;
                    string obfsJson = !string.IsNullOrEmpty(q["obfs"]) ? $@",""obfs"":{{""type"":""{q["obfs"]}"",""password"":""{q["obfs-password"]}""}}" : "";
                    outboundObj = $@"{{""tag"":""proxy"",""protocol"":""hysteria2"",""settings"":{{""servers"":[{{""address"":""{uri.Host}"",""port"":{uri.Port},""password"":""{uri.UserInfo}""{obfsJson}}}]}},""streamSettings"":{{""network"":""udp"",""security"":""tls"",""tlsSettings"":{{""serverName"":""{sni}"",""allowInsecure"":true}}}}}}";
                }
            }
            catch { throw; }

            return $@"{{{header}""outbounds"":[{outboundObj},{{""protocol"":""freedom"",""tag"":""direct""}}],{routing}}}";
        }
    }
}