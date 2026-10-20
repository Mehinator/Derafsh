<p align="center">
  <img src="docs/logo.png" width="96" alt="Derafsh Kaviani banner">
</p>

<h1 align="center">درفش · Derafsh</h1>

<p align="center">
  کلاینت رایگان Xray برای ویندوز و اندروید، با کانفیگ‌های تازه از تلگرام — هر ۴ ساعت<br>
  A free Xray client for Windows and Android with fresh configs from Telegram, refreshed every 4 hours
</p>

<p align="center">
  <a href="https://github.com/Mehinator/Derafsh/releases/latest"><b>⬇ دانلود آخرین نسخه / Download the latest release</b></a>
</p>

<p align="center"><img src="docs/screenshot.png" width="720" alt="Derafsh on Windows"></p>

نام «درفش» از درفش کاویانی آمده: پرچمی که کاوه‌ی آهنگر در برابر ستم ضحاک برافراشت. این برنامه رایگان است و برای این ساخته شده که همه بتوانند به اینترنت آزاد وصل شوند.

---

## فارسی

### دانلود و اجرا (ویندوز)

1. از بخش [Releases](https://github.com/Mehinator/Derafsh/releases/latest) فایل `Derafsh-v…-Windows-x64.zip` را دانلود کنید.
2. فایل را از حالت فشرده خارج کنید و `Derafsh.Client.exe` را اجرا کنید. نصب لازم نیست.
3. اگر ویندوز پیام «Windows protected your PC» داد: روی **More info** و بعد **Run anyway** بزنید (برنامه امضای دیجیتال ندارد).

نیازمندی: ویندوز ۱۰ (نسخه ۱۸۰۹ به بعد) یا ویندوز ۱۱، ۶۴ بیتی.

### طرز استفاده

| دکمه | کار |
|---|---|
| **Test all** | همه‌ی کانفیگ‌های لیست را تست می‌کند و سریع‌ترین را انتخاب می‌کند |
| **Connect** | به کانفیگ انتخاب‌شده وصل می‌شود (در ویندوز پروکسی سیستم، در اندروید VPN روشن می‌شود و IP و کشور واقعی نمایش داده می‌شود) |
| **Refresh list** | تازه‌ترین کانفیگ‌ها را دانلود می‌کند (نتیجه‌ی تست‌ها حفظ می‌شود) |
| **Remove dead** | کانفیگ‌هایی را که در تست جواب ندادند کنار می‌گذارد؛ کانفیگ‌های آنلاین فقط ۲۴ ساعت پنهان می‌شوند |
| **Add config** | لینک‌های خودتان (vless / vmess / ss / trojan / hysteria2) را به لیست شخصی اضافه می‌کند |
| **Ping** / **Gemini** | سرعت یک کانفیگ را تست می‌کند / بررسی می‌کند که Gemini از طریق آن باز می‌شود یا نه |
| **✧** / **Save** | کانفیگ را به علاقه‌مندی‌ها اضافه می‌کند / در لیست شخصی ذخیره می‌کند |

زیر هر کانفیگ نوشته شده از کدام کانال تلگرام آمده است.

### اندروید

1. از بخش [Releases](https://github.com/Mehinator/Derafsh/releases/latest) فایل `Derafsh-v…-Android-arm64.apk` را روی گوشی دانلود کنید.
2. فایل را باز کنید. اگر اندروید اجازه‌ی «نصب برنامه‌های ناشناس» خواست، برای همان مرورگر یا فایل‌منیجر اجازه بدهید و **Install** را بزنید.
3. در درفش یک کانفیگ را انتخاب کنید و **Connect** را بزنید. بار اول اندروید می‌پرسد که درفش اجازه‌ی ساختن VPN دارد یا نه: **OK** را بزنید.

نیازمندی: اندروید ۱۰ به بعد، گوشی ۶۴ بیتی (arm64؛ تقریباً همه‌ی گوشی‌های چند سال اخیر).

اگر می‌خواهید از برنامه‌ی دیگری استفاده کنید، همین لیست را می‌توانید در **v2rayNG**، **Hiddify** یا **NekoBox** هم اضافه کنید؛ هر ۴ ساعت خودکار به‌روز می‌شود:

1. در برنامه به بخش **Subscription group setting** (تنظیمات اشتراک) بروید و **+** را بزنید.
2. این آدرس را وارد کنید:
   ```
   https://cdn.jsdelivr.net/gh/Mehinator/Derafsh@main/derafsh_sub.txt
   ```
3. **Update subscription** را بزنید، کانفیگ‌ها را تست کنید و به یکی از سریع‌ها وصل شوید.

### کانفیگ‌ها از کجا می‌آیند؟

اسکریپت [`collector.py`](collector.py) هر ۴ ساعت روی GitHub Actions اجرا می‌شود: صفحه‌ی عمومی چند کانال تلگرام (فهرست در همان فایل) را می‌خواند، لینک‌های خراب و تکراری را حذف می‌کند و سه فایل می‌سازد:

| فایل | محتوا |
|---|---|
| [`derafsh_servers.txt`](derafsh_servers.txt) | لیست کانفیگ‌ها، جدیدترین‌ها اول |
| [`derafsh_sources.json`](derafsh_sources.json) | منبع (کانال) هر کانفیگ |
| [`derafsh_sub.txt`](derafsh_sub.txt) | همان لیست به شکل اشتراک استاندارد (base64) برای اپ‌های دیگر |

### نکته‌ی امنیتی

کانفیگ‌ها رایگان و از کانال‌های عمومی هستند و صاحب سرورها ناشناس‌اند. برای کارهای حساس (بانک، حساب‌های مهم) فقط از سایت‌های HTTPS استفاده کنید و رمزهای مهم را از طریق کانفیگ ناشناس وارد نکنید.

---

## English

**Derafsh** collects free V2Ray/Xray configs from public Telegram channels every 4 hours (GitHub Actions + [`collector.py`](collector.py)), and the app lets you test them (real ping through Xray, Gemini reachability), keep favorites, and connect with one click — through the system proxy on Windows, as a VPN on Android.

- **Download:** [Releases](https://github.com/Mehinator/Derafsh/releases/latest) — `Windows-x64.zip` (unzip and run, Windows 10 1809+) and `Android-arm64.apk` (Android 10+, 64-bit phones).
- **Protocols:** VLESS (TCP, WebSocket, gRPC, XHTTP, HTTPUpgrade; TLS, REALITY), VMess, Shadowsocks, Trojan, Hysteria2 — on **Xray-core 26.3.27**. Configs the engine can't start are dropped by the collector.
- **Other apps:** the same list works as a subscription in v2rayNG, Hiddify or NekoBox: `https://cdn.jsdelivr.net/gh/Mehinator/Derafsh@main/derafsh_sub.txt`.
- **Safety:** these are free servers run by unknown people. Prefer HTTPS sites and don't enter important passwords over them.

### Build from source

Requirements: Windows 10 1809+, the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) with the MAUI Windows workload (`dotnet workload install maui-windows`), or Visual Studio with the .NET MAUI workload.

```powershell
# run from source
dotnet build Derafsh.Client -c Debug
# self-contained release folder (what the release zip contains)
dotnet publish Derafsh.Client -f net10.0-windows10.0.19041.0 -p:TargetFrameworks=net10.0-windows10.0.19041.0 -c Release -r win-x64 --self-contained true `
  -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true -p:PublishReadyToRun=true -o publish\Derafsh
# Android APK (needs the MAUI Android workload and a JDK)
dotnet build Derafsh.Client -f net10.0-android -c Release
```

The first build downloads Xray-core (and, for Android, hev-socks5-tunnel) from their official releases and checks their SHA-256.
Pushing a `v*` tag runs [`release.yml`](.github/workflows/release.yml), which builds both and attaches them to the release.

The config collector: `pip install requests` then `python collector.py`.

### Project layout

| Path | What it is |
|---|---|
| `Derafsh.Client/` | .NET MAUI app (`MainPage.xaml` UI, `Service/ConfigService.cs` link parsing and Xray config, `XrayProc.cs` engine process, `Resources/Styles/Theme.xaml` design tokens) |
| `Derafsh.Client/Platforms/Android/DerafshVpn.cs` | Android VPN: TUN → hev-socks5-tunnel → Xray SOCKS |
| `collector.py`, `.github/workflows/update_configs.yml` | the 4-hourly config collector |
| `.github/workflows/release.yml` | release builds (Windows zip, signed APK) |

### License

MIT — see [LICENSE](LICENSE). Bundled third-party software (Xray-core, MPL-2.0; Vazirmatn font, OFL-1.1) is listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
