namespace Derafsh.Client.Models
{
    public class ConfigDto
    {
        public string? add { get; set; } // آدرس
        public object? port { get; set; } // پورت (گاهی عدد، گاهی رشته)
        public string? id { get; set; } // UUID
        public int? aid { get; set; } // AlterId
        public string? scy { get; set; } // Security
        public string? net { get; set; } // Network (ws, tcp, ...)
        public string? tls { get; set; } // TLS setting
        public string? path { get; set; }
        public string? host { get; set; }
        public string? sni { get; set; }
        public string? fp { get; set; } // Fingerprint
    }
}