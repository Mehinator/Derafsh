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

برنامه دو صفحه دارد:

**صفحه‌ی اصلی** فقط یک دکمه‌ی گرد **اتصال** دارد. درفش خودش سرورها را روی **همان اینترنتی که الان وصل هستید** تست می‌کند و به سریع‌ترین سروری که جواب بدهد وصل می‌شود. اگر تیک **«فقط سروری که جمینی را باز می‌کند»** را بزنید، سریع‌ترین سروری انتخاب می‌شود که جمینی را هم باز کند.

**صفحه‌ی سرورها** (دکمه‌ی «سرورها» بالای صفحه) برای کارهای بیشتر است. از منوی **لیست** یکی را انتخاب کنید:

| لیست | چه چیزی نشان می‌دهد | دکمه‌ها |
|---|---|---|
| **آنلاین** | سرورهای رایگانی که روی همین اینترنت جواب می‌دهند، سریع‌ترین بالا | **تست دوباره** · **دریافت لیست تازه** · **ذخیره به‌عنوان لیست** |
| **شخصی** | کانفیگ‌هایی که خودتان اضافه کرده‌اید | **تست دوباره** · **افزودن کانفیگ** · **ذخیره به‌عنوان لیست** |
| **لیست‌های ذخیره‌شده** | هر لیستی که با اسم دلخواه ذخیره کرده‌اید، مثلاً «ایرانسل» یا «وای‌فای خانه» | **تست دوباره** · **حذف این لیست** |

روی هر سرور بزنید تا برای اتصال انتخاب شود (در صفحه‌ی اصلی، **خودکار** دوباره انتخاب را به برنامه می‌سپارد). دکمه‌های هر سرور: **پینگ** (سرعت)، **جمینی** (آیا جمینی باز می‌شود)، **اشتراک** (فرستادن لینک کانفیگ برای دیگران)، **✧** (علاقه‌مندی) و **+ شخصی** (افزودن به لیست شخصی). زیر هر سرور آدرس آن و کانال تلگرامی که از آن آمده نوشته شده است.

با دکمه‌ی **«اشتراک سالم‌ها»** در صفحه‌ی سرورها، لینک همه‌ی سرورهایی که الان جواب می‌دهند یکجا برای دیگران فرستاده می‌شود.

کانفیگ‌های رایگان زود فیلتر می‌شوند: سروری که چند دقیقه پیش جواب می‌داد ممکن است الان جواب ندهد. این مشکل برنامه نیست؛ دوباره **اتصال** یا **تست دوباره** را بزنید.

در ویندوز، درفش موقع اتصال پروکسی سیستم را روی خودش می‌گذارد و موقع قطع، تنظیم قبلی شما را برمی‌گرداند.

### Aether (WARP) — پشتیبانی از لینک‌های aether://

درفش از هسته‌ی [Aether](https://github.com/patterniha/Aether) (مثل پترن/PattN) پشتیبانی می‌کند: لینک‌های `aether://` را از کانال‌ها می‌گیرد، یا خودتان از **افزودن کانفیگ** اضافه کنید. پروتکل‌های MASQUE (HTTP/3 و HTTP/2)، WireGuard، Gool و WARP-in-WARP، fragmentation، noize و حالت‌های سایفون/تور، همه مطابق لینک اجرا می‌شوند. شناسه‌ی WARP روی دستگاه ذخیره می‌شود تا هر بار ثبت‌نام تکراری نشود.

تیک **«خروج WARP از سرور (Aether)»** در صفحه‌ی اصلی: وقتی روشن باشد و به یک کانفیگ WARP وصل شوید، ترافیک خود WARP از داخل سریع‌ترین سرور سالم رد می‌شود (WARP over VLESS)، نه مستقیم از اینترنت ایران — برای شبکه‌هایی که WARP مستقیم را می‌بندند.

موتور Xray نیز فورک [patterniha/Xray-core](https://github.com/patterniha/Xray-core) است (همان که پترن استفاده می‌کند): اتصال به کانفیگ‌های VLESS/Trojan بدون رمزنگاری روی آدرس‌های عمومی کار می‌کند و پارامترهای `cs=` (cipherSuites) و `alpn` لینک‌ها با قانون http/1.1 برای ws اعمال می‌شوند — همان روشی که تست‌ها را در پترن درست نشان می‌دهد.

### اندروید

1. از بخش [Releases](https://github.com/Mehinator/Derafsh/releases/latest) یکی از این فایل‌ها را روی گوشی دانلود کنید:
   - `Derafsh-v…-Android-arm64.apk`: بیشتر گوشی‌های چند سال اخیر
   - `Derafsh-v…-Android-armv7.apk`: گوشی‌های قدیمی‌تر و ارزان‌تر (۳۲ بیتی). اگر arm64 نصب نشد، این را بگیرید
   - `Derafsh-v…-Android-universal.apk`: اگر نمی‌دانید کدام؛ روی همه کار می‌کند ولی حجمش بیشتر است
2. فایل را باز کنید. اگر اندروید اجازه‌ی «نصب برنامه‌های ناشناس» خواست، برای همان مرورگر یا فایل‌منیجر اجازه بدهید و **Install** را بزنید.
3. در درفش یک کانفیگ را انتخاب کنید و **Connect** را بزنید. بار اول اندروید می‌پرسد که درفش اجازه‌ی ساختن VPN دارد یا نه: **OK** را بزنید.

نیازمندی: اندروید ۷ به بعد.

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

**Derafsh** collects free V2Ray/Xray configs from public Telegram channels every 4 hours (GitHub Actions + [`collector.py`](collector.py)), and the app (in Persian) connects with one button: it tests the configs on the network you're on and picks the fastest one that answers, optionally only ones that open Gemini. A second screen lists the servers (online, personal, and lists you save under your own names) for testing and choosing by hand — through the system proxy on Windows, as a VPN on Android.

- **Download:** [Releases](https://github.com/Mehinator/Derafsh/releases/latest) — `Windows-x64.zip` (unzip and run, Windows 10 1809+); Android 7+: `Android-arm64.apk` (most phones), `Android-armv7.apk` (32-bit phones) or `Android-universal.apk` (both).
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
# Android APK (needs the MAUI Android workload and a JDK); DerafshAndroidAbis = arm64 (default), armv7 or universal
dotnet build Derafsh.Client -f net10.0-android -c Release -p:DerafshAndroidAbis=arm64
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
