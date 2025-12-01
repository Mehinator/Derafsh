namespace Derafsh.Client.Models
{
    public class ConfigDto
    {
        public string? v { get; set; } // نسخه (معمولاً "2")
        public string? ps { get; set; } // *** این همون اسم کانفیگه که ارور می‌داد ***
        public string? add { get; set; }
        public object? port { get; set; }
        public string? id { get; set; }
        public object? aid { get; set; }
        public string? scy { get; set; }
        public string? net { get; set; }
        public string? type { get; set; }
        public string? host { get; set; }
        public string? path { get; set; }
        public string? tls { get; set; }
        public string? sni { get; set; }
        public string? alpn { get; set; }
        public string? fp { get; set; }
    }
}