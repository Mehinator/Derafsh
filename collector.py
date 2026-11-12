import requests
import re
import os
import sys
import json
import html
import base64
import time
import shlex
import bisect
import hashlib
import tempfile
import ipaddress
import subprocess
from collections import Counter, namedtuple
from itertools import zip_longest
from urllib.parse import quote, unquote, urlsplit, parse_qsl

# لیست کانال‌ها (اصلاح شده و استاندارد)
# نکته: لینک آخر رو درست کردم چون https و /s/ نداشت
CHANNELS = [
    "https://t.me/s/Daily_Configs",
    "https://t.me/s/V2rayNGn",
    "https://t.me/s/PrivateVPNs",
    "https://t.me/s/DirectVPN",
    "https://t.me/s/mehrosaboran",
    "https://t.me/s/Cook_Vpn",
    "https://t.me/s/filembad",
    "https://t.me/s/chillguy_vpn",
    "https://t.me/s/Express_freevpn",
    "https://t.me/s/i10VPN",
]

# از هر کانال چند صفحه (هر صفحه حدود ۲۰ پیام آخر)
TELEGRAM_PAGES = 2

# لیست‌های عمومی گیت‌هاب (جمع‌کننده‌های دیگه که هر ۱۵ دقیقه تا ۴ ساعت تازه می‌شن و از صدها کانال جمع می‌کنن؛
# همون‌هایی که برنامه‌هایی مثل MEH VPN هم می‌گیرن). فقط وقتی اضافه می‌شن که تست (پایین) اجرا بشه، چون بیشترشون
# مرده‌ان و بدون تست فقط لیست رو شلوغ می‌کنن.
# عدد سوم: از هر لیست چندتای اول. از تست ۱۲ نوامبر ۲۰۲۶ (درصد سالم از گیت‌هاب): mhcfp ۸۷٪، barry-far ۳۷٪،
# MohammadBahemmat ۳۳٪، SoliSpirit ۹٪، ALIILAPRO ۷٪، PSG و arshiacomplus ۶٪؛ پرسالم‌ها کامل، کم‌سالم‌ها کمتر.
GITHUB_LISTS = [
    ("rzashakeri", "https://raw.githubusercontent.com/rzashakeri/v2ray-configs/main/configs/all.txt", 500),
    ("mhcfp", "https://raw.githubusercontent.com/mhcfpTopic/config/main/xray_final.txt", 3000),
    ("MohammadBahemmat", "https://raw.githubusercontent.com/MohammadBahemmat/V2ray-Collector/main/Tested.txt", 500),
    ("barry-far", "https://raw.githubusercontent.com/barry-far/V2ray-Config/main/Sub1.txt", 500),
    ("barry-far", "https://raw.githubusercontent.com/barry-far/V2ray-Config/main/Sub2.txt", 500),
    ("barry-far", "https://raw.githubusercontent.com/barry-far/V2ray-Config/main/Sub3.txt", 500),
    ("PSG", "https://raw.githubusercontent.com/itsyebekhe/PSG/main/config.txt", 200),
    ("SoliSpirit", "https://raw.githubusercontent.com/SoliSpirit/v2ray-configs/main/Protocols/vless.txt", 300),
    ("SoliSpirit", "https://raw.githubusercontent.com/SoliSpirit/v2ray-configs/main/Protocols/trojan.txt", 300),
    ("SoliSpirit", "https://raw.githubusercontent.com/SoliSpirit/v2ray-configs/main/Protocols/vmess.txt", 300),
    ("SoliSpirit", "https://raw.githubusercontent.com/SoliSpirit/v2ray-configs/main/Protocols/ss.txt", 300),
    ("ALIILAPRO", "https://raw.githubusercontent.com/ALIILAPRO/v2rayNG-Config/main/sub.txt", 300),
    ("arshiacomplus", "https://raw.githubusercontent.com/arshiacomplus/v2rayExtractor/refs/heads/main/mix/sub.html", 200),
    ("Masir_Sefid", "https://raw.githubusercontent.com/masir-sefid/Sub/main/@Masir_Sefid.txt", 200),
    # از ۱۲ نوامبر ۲۰۲۶ (همه همون روز به‌روز شده بودن)
    ("MatinGhanbari", "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/v2ray/super-sub.txt", 300),
    ("4n0nymou3", "https://raw.githubusercontent.com/4n0nymou3/multi-proxy-config-fetcher/main/configs/proxy_configs_tested.txt", 300),
    ("V2RayRoot", "https://raw.githubusercontent.com/V2RayRoot/V2RayConfig/main/Config/vless.txt", 300),
    ("TGParse", "https://raw.githubusercontent.com/Surfboardv2ray/TGParse/main/splitted/vless", 400),
    ("mahdibland", "https://raw.githubusercontent.com/mahdibland/V2RayAggregator/master/sub/sub_merge.txt", 300),
]

# هر سرور توی چندتا کانال و لیست پخش شده: {backend_key: {اسم منبع}}. کانفیگی که همه جا گذاشتن، کاربر زیادی داره و
# شلوغه؛ توی ترتیب پایین‌تر می‌ره (build_tested_list)
SHARED = {}
MAX_CANDIDATES = 7000   # سقف کانفیگ‌هایی که هر بار تست می‌شن (حدود ۱۸ دقیقه)

# IPهای ایران (همون فایل برنامه): سروری که توی ایرانه (مثلاً سرور واسط) از بیرون ایران قابل قضاوت نیست
IRAN_IP_FILE = os.path.join("Derafsh.Client", "Resources", "Raw", "iran_ip.txt")

# تست قبل از ساختن لیست: برنامه‌ی tools/ConfigTester هر لینک رو با کد خود درفش (ConfigService) به Xray تبدیل می‌کنه و
# از داخلش generate_204 گوگل و یه دانلود واقعی رو امتحان می‌کنه. فقط اون‌هایی که جواب می‌دن توی لیست می‌مونن.
# روی گیت‌هاب (بیرون از ایران) اجرا می‌شه، پس فیلتر ایران رو نمی‌سنجه؛ سرورهای مرده رو کنار می‌ذاره و بقیه رو گوشی
# روی اینترنت خودش تست می‌کنه. بدون این دو متغیر (مثلاً اجرای دستی) مثل قبل، بدون تست.
TESTER = os.environ.get("DERAFSH_TESTER")   # مثلاً "dotnet tester/ConfigTester.dll"
XRAY = os.environ.get("DERAFSH_XRAY")
DRY_RUN = bool(os.environ.get("DERAFSH_DRY_RUN"))   # فقط تست و آمار، لیست عوض نمی‌شه
MIN_ALIVE = 150   # کمتر از این زنده بود یعنی تست خودش درست کار نکرده؛ لیست مثل قبل ساخته می‌شه
# چند اجرای پشت سر هم هر کانفیگ سالم بوده (کش GitHub Actions، توی ریپو نمی‌ره). کانفیگی که چند روزه زنده مونده کمتر
# وسط ویدیو می‌میره تا کانفیگی که همین الان توی یه کانال گذاشتن و تا چند ساعت دیگه پر یا بسته می‌شه.
STATS_FILE = os.environ.get("DERAFSH_STATS")

# فایل خروجی
OUTPUT_FILE = "derafsh_servers.txt"

# فایل منبع: هر کانفیگ به کدوم کانال تلگرام تعلق داره
SOURCES_FILE = "derafsh_sources.json"

# مکان هر کانفیگ («DE|Berlin»): جایی که ترافیک ازش بیرون می‌آد و سایت‌ها می‌بینن، برای انتخاب کشور و شهر توی برنامه
LOCATIONS_FILE = "derafsh_locations.json"

# لینک اشتراک (subscription) استاندارد base64، برای v2rayNG / Hiddify / NekoBox روی اندروید
SUB_FILE = "derafsh_sub.txt"

# سقف تعداد کانفیگ: ۵۰۰ تای بهتر، نه ۲۰۰۰ تا که گوشی وقتش رو سر تست‌شون بذاره
MAX_CONFIGS = 500
PER_EXIT = 2          # از هر IP خروجی حداکثر دوتا، تا وقتی یه سرور می‌میره زاپاس‌ها هم باهاش نمیرن
PER_GROUP = 3         # از هر رنج IP خروجی (/24، معمولاً یه اپراتور) حداکثر سه‌تا؛ در یه اجرا ۳۲ کانفیگ از یه رنج اومده بود
CITY_RESERVE = 6      # از هر شهر تا این‌قدر از بهترین‌هاش، تا انتخاب شهر توی برنامه (مثلاً برلین) خالی نمونه
COUNTRY_RESERVE = 30  # ... ولی از هر کشور حداکثر این‌قدر از راه سهم شهرها
RESERVE_SHARE = 0.6   # سهم شهرها حداکثر ۶۰٪ لیست، نوبتی (اول یکی از هر شهر، بعد دومی...)؛ بقیه فقط به ترتیب کیفیت
IRAN_KEEP = 25        # سرورهای داخل ایران (واسط) که از بیرون قابل تست نیستن، ته لیست
CDN_ASN = 13335       # خروجی کلادفلر (مثلاً Workers): مکانش به جای کاربر بستگی داره، پس مکان ثابت نداره
# خروجی توی این کشورها به درد کاربر ایرانی نمی‌خوره: ترافیک از خود ایران بیرون می‌آد و فیلتر سر جاشه
USELESS_EXITS = {"IR"}
# کشورهای دور از ایران (پینگ بالا)؛ بقیه (اروپا، ترکیه، خاورمیانه، قفقاز، ...) نزدیک حساب می‌شن
FAR_COUNTRIES = {"US", "CA", "MX", "BR", "AR", "CL", "CO", "PE", "JP", "KR", "CN", "HK", "TW", "SG", "MY", "TH", "VN",
                 "ID", "PH", "AU", "NZ", "ZA", "NG", "KE"}

# فقط پروتکل‌هایی که برنامه درفش می‌تونه باهاشون وصل بشه
# مهم: (?:...) لازمه. با پرانتز معمولی، re.findall فقط اسم پروتکل (مثلاً "vless") رو برمی‌گردوند
# نه کل لینک رو؛ برای همین تا حالا هیچ کانفیگی از تلگرام جمع نمی‌شد
LINK_PATTERN = re.compile(r'(?:vmess|vless|ss|trojan|hysteria2)://[^\s<>"]+')

# کاراکترهایی که Regex برنامه (ConfigService.cs) قبول می‌کنه؛ بقیه percent-encode می‌شن
# تا برنامه دقیقاً همون لینکی رو بخونه که اینجا ذخیره شده
APP_SAFE_CHARS = set("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._~:/?#@!$&'()*+,;=%")

# شکل UUID داره ولی هگز نیست (کانفیگ قلابی/خراب) → Xray اصلاً اجراش نمی‌کنه
UUID_SHAPE = re.compile(r'^[^-]{8}-[^-]{4}-[^-]{4}-[^-]{4}-[^-]{12}$')
UUID_HEX = re.compile(r'^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$')


def bad_uuid(value):
    return bool(UUID_SHAPE.match(value)) and not UUID_HEX.match(value)


HEADERS = {"User-Agent":"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36"}


SS_LEGACY = re.compile(r"^([\w.+-]+):(.+)@(\[[0-9a-fA-F:]+\]|[^@:\s/]+):(\d+)/?$")


def fix_ss_link(link):
    """لینک‌های ss:// که درست نوشته نشدن (بیشتر از لیست‌های عمومی):
    - vless یا vmess که پیشوندشون اشتباهی ss:// شده (UUID به جای روش و رمز، یا JSON مثل vmess)
    - شکل قدیمی ss که کل method:password@host:port با هم base64 شده؛ به شکل استاندارد امروزی (SIP002) برمی‌گرده"""
    if not link.startswith("ss://"):
        return link
    body, sep, name = link[5:].partition("#")
    if "@" in body:
        user = unquote(body.split("@", 1)[0])
        if UUID_HEX.match(user) and re.search(r"[?&](encryption|security|type)=", body):
            return "vless://" + link[5:]
        return link
    data = body.split("?", 1)[0].rstrip("/").replace("-", "+").replace("_", "/")
    try:
        text = base64.b64decode(data + "=" * (-len(data) % 4)).decode("utf-8").strip()
    except Exception:
        return link
    if text.startswith("{") and '"add"' in text:
        return "vmess://" + body.split("?", 1)[0] + sep + name
    m = SS_LEGACY.match(text)
    if not m:
        return link
    method, password, host, port = m.groups()
    user = base64.urlsafe_b64encode(f"{method}:{password}".encode("utf-8")).decode("ascii").rstrip("=")
    return f"ss://{user}@{host}:{port}" + sep + name


def clean_link(link):
    """لینک رو یکدست می‌کنه تا برنامه بتونه بخونه و وصل بشه. لینک خراب یا ناقص = None"""
    link = link.strip()
    # علامت‌های چسبیده به آخر لینک (مثلاً لینکی که توی پرانتز نوشته شده) حذف می‌شن،
    # ولی پرانتزی که جفتش توی خود لینکه (مثل اسم "(S102)") می‌مونه
    while link and link[-1] in ")]}\"'`":
        pair = {')': '(', ']': '[', '}': '{'}.get(link[-1])
        if pair and link.count(pair) >= link.count(link[-1]):
            break
        link = link[:-1]
    link = fix_ss_link(link)
    body, sep, name = link.partition('#')
    try:
        if body.startswith("vmess://"):
            # برنامه فقط base64 استاندارد (با = در آخر) رو می‌فهمه
            data = body[8:].replace('-', '+').replace('_', '/')
            text = base64.b64decode(data + '=' * (-len(data) % 4)).decode('utf-8-sig')
            info = json.loads(text)
            if not info.get("add") or not info.get("port") or bad_uuid(str(info.get("id", ""))):
                return None
            body = "vmess://" + base64.b64encode(text.encode('utf-8')).decode()
        else:
            body = ''.join(c if c in APP_SAFE_CHARS else quote(c) for c in body)
            parts = urlsplit(body)
            if not parts.hostname or parts.port is None:
                return None
            if body.startswith("vless://") and bad_uuid(unquote(parts.username or "")):
                return None
    except Exception:
        return None
    if sep:
        # اسم کانفیگ (بعد از #) هم percent-encode می‌شه؛ برنامه موقع نمایش decode می‌کنه
        name = quote(unquote(name), safe='')
        if not name:
            sep = ''  # لینکی که با # خالی تموم شده
    return body + sep + name


# پارامترهایی که تعیین می‌کنن «کدوم سرور» و «از چه راهی»؛ بقیه (اسم، fp، alpn، ترتیب پارامترها، کدگذاری) فرقی نمی‌سازن
KEY_PARAMS = ("type", "security", "sni", "host", "path", "servicename", "mode", "pbk", "sid", "flow",
              "headertype", "obfs", "obfs-password", "encryption")


def server_key(link):
    """هویت خود سرور پشت یه لینک، مستقل از اسم و ترتیب پارامترها و کدگذاری.
    یه سرور که چند کانال هر کدوم با اسم خودشون گذاشتن، فقط یه بار حساب می‌شه."""
    body = link.split("#", 1)[0].strip()
    scheme = body.split("://", 1)[0].lower()
    try:
        if scheme == "vmess":
            # اسم vmess (ps) داخل خود base64 هست؛ برای همین لینک‌های هم‌سرور متن متفاوت دارن
            data = body[8:].replace("-", "+").replace("_", "/")
            info = json.loads(base64.b64decode(data + "=" * (-len(data) % 4)).decode("utf-8-sig"))
            g = lambda k: str(info.get(k) or "").strip().lower()
            return ("vmess", g("add"), g("port"), g("id"), g("net") or "tcp", g("type") or "none",
                    g("host"), g("path") or "/", g("tls"), g("sni"))
        parts = urlsplit(body)
        q = {k.lower(): unquote(v).strip() for k, v in parse_qsl(parts.query)}
        user = unquote(parts.username or "")
        if scheme == "ss":
            # روش:رمز یا به شکل base64 یا خام؛ هر دو یکی حساب می‌شن
            try:
                raw = user.replace("-", "+").replace("_", "/")
                decoded = base64.b64decode(raw + "=" * (-len(raw) % 4)).decode("utf-8")
                if ":" in decoded:
                    user = decoded
            except Exception:
                pass
        elif scheme == "vless":
            user = user.lower()
        q.setdefault("type", "tcp")
        if q.get("type") in ("ws", "httpupgrade", "xhttp", "splithttp") and not q.get("path"):
            q["path"] = "/"
        key = [scheme, user, (parts.hostname or "").lower(), parts.port]
        key += [q.get(k, "") if k in ("path", "pbk", "sid", "obfs-password") else q.get(k, "").lower() for k in KEY_PARAMS]
        return tuple(key)
    except Exception:
        return body


def interleave(groups):
    """از هر گروه یکی به نوبت، تا اول لیست فقط مال یه منبع نباشه"""
    out = []
    for group in zip_longest(*groups):
        out.extend(link for link in group if link)
    return out


def channel_page_links(url):
    """لینک‌های چند صفحه‌ی آخر یه کانال، به ترتیب زمان (قدیمی‌تر اول)؛ None یعنی کانال پیام عمومی نداره"""
    links, before = [], None
    for _ in range(TELEGRAM_PAGES):
        response = requests.get(url + (f"?before={before}" if before else ""), timeout=15, headers=HEADERS)
        if response.status_code != 200:
            if before:
                break
            raise Exception(f"HTTP {response.status_code}")
        # تلگرام & رو توی HTML به شکل &amp; می‌نویسه؛ اول برش می‌گردونیم
        page = html.unescape(response.text)
        if "tgme_widget_message" not in page:
            if before:
                break
            return None
        links = [c for c in (clean_link(m) for m in LINK_PATTERN.findall(page)) if c] + links
        ids = [int(x) for x in re.findall(r'data-post="[^"/]+/(\d+)"', page)]
        if not ids:
            break
        before = min(ids)   # صفحه‌ی قبلی: پیام‌های قدیمی‌تر از این
    return links


def get_new_configs(sources):
    per_channel = []

    print("🚀 Scraping Telegram Channels...")
    for url in CHANNELS:
        name = url.rstrip('/').split('/')[-1]
        try:
            links = channel_page_links(url)
            if links is None:
                print(f"   ⚠️ {name}: No public messages (channel deleted, private, or preview disabled?)")
                continue
            # پیام‌های جدیدتر پایین صفحه‌ان؛ برعکس می‌کنیم که جدیدترها اول باشن، و هر سرور فقط یه بار
            unique = {}
            for link in reversed(links):
                unique.setdefault(server_key(link), link)
            links = list(unique.values())
            for link in links:
                sources.setdefault(link, name)
                SHARED.setdefault(backend_key(link), set()).add(name)
            per_channel.append(links)
            print(f"   ✅ {name}: Found {len(links)}")
        except Exception as e:
            print(f"   ❌ {name}: {e}")

    return interleave(per_channel)


def get_list_configs(sources):
    """کانفیگ‌های لیست‌های عمومی گیت‌هاب (متن ساده یا base64)"""
    per_list = []
    print("🚀 Fetching public GitHub lists...")
    for name, url, cap in GITHUB_LISTS:
        try:
            response = requests.get(url, timeout=30, headers=HEADERS)
            if response.status_code != 200:
                print(f"   ❌ {name}: HTTP {response.status_code} ({url})")
                continue
            text = response.text
            if "://" not in text[:5000]:   # لینک اشتراک base64
                data = re.sub(r"\s+", "", text).replace("-", "+").replace("_", "/")
                try:
                    text = base64.b64decode(data + "=" * (-len(data) % 4)).decode("utf-8", "ignore")
                except Exception:
                    pass
            # بعضی لیست‌ها & رو به شکل HTML (&amp;) نوشتن و لینک‌ها خراب می‌شن
            text = html.unescape(text)
            unique = {}
            for link in (c for c in (clean_link(m) for m in LINK_PATTERN.findall(text)) if c):
                unique.setdefault(server_key(link), link)
            links = list(unique.values())[:cap]
            for link in links:
                sources.setdefault(link, name)
                SHARED.setdefault(backend_key(link), set()).add(name)
            per_list.append(links)
            print(f"   ✅ {name}: Found {len(links)} ({url.rsplit('/', 1)[-1]})")
        except Exception as e:
            print(f"   ❌ {name}: {e}")
    return interleave(per_list)


# نتیجه‌ی تست یه کانفیگ؛ مکان (کشور، شهر، شبکه، IP خروجی) فقط برای سالم‌ها و اگه پیدا شد
Result = namedtuple("Result", "status ping kbps country city asn ip")
UNTESTED = Result("untested", 0, 0, "", "", 0, "")


def test_configs(links):
    """نتیجه‌ی تست هر لینک: {لینک: Result}؛ None یعنی تست اجرا نشد"""
    if not TESTER or not XRAY:
        print("ℹ️ No tester (DERAFSH_TESTER / DERAFSH_XRAY): the list is built without testing")
        return None
    work = tempfile.mkdtemp(prefix="derafsh-")
    candidates_path = os.path.join(work, "candidates.txt")
    results_path = os.path.join(work, "results.tsv")
    with open(candidates_path, "w", encoding="utf-8") as f:
        f.write("\n".join(links) + "\n")
    print(f"🧪 Testing {len(links)} configs with Xray...")
    try:
        subprocess.run(shlex.split(TESTER) + [candidates_path, results_path, XRAY, "300", "20"], check=True, timeout=27 * 60)
        results = {}
        reasons = {}
        with open(results_path, encoding="utf-8") as f:
            for line in f:
                parts = line.rstrip("\n").split("\t")
                if len(parts) < 2:
                    continue
                if parts[1] == "ok" and len(parts) > 3:
                    loc = (parts + [""] * 8)[4:8]
                    results[parts[0]] = Result("ok", int(parts[2]), int(parts[3]), loc[0].upper(), loc[1],
                                               int(loc[2]) if loc[2].isdigit() else 0, loc[3])
                else:
                    results[parts[0]] = UNTESTED._replace(status=parts[1])
                    if parts[1] == "invalid":
                        why = parts[2] if len(parts) > 2 else "?"
                        reasons[why] = reasons.get(why, 0) + 1
        # چیزهایی که برنامه هنوز نمی‌تونه باهاشون وصل بشه (برای بهتر کردن ConfigService)
        if reasons:
            print("   Configs the app can't use yet, most common reasons:")
            for why, n in sorted(reasons.items(), key=lambda x: -x[1])[:10]:
                print(f"     {n} × {why}")
        return results
    except Exception as e:
        print(f"❌ Tester failed: {e}")
        return None


def spread_key(link):
    """برای پخش کردن: آدرس سرور + اسم سایتی که پشتشه (sni/host)؛ مثلاً ده‌تا ورکر مختلف روی یه IP کلادفلر یکی حساب نمی‌شن"""
    key = server_key(link)
    if not isinstance(key, tuple):
        return key
    if key[0] == "vmess":
        return (key[1], key[9] or key[6])
    return (key[2], key[6] or key[7])


def speed_rank(kbps):
    # سرعت از گیت‌هاب (۱۵۰ کیلوبایت): بیشتر توان خود سرور رو نشون می‌ده، نه سرعتی که از ایران می‌گیره؛
    # ۳۰۰ کیلوبایت بر ثانیه برای ویدیو بسه، بیشترش از ایران فرقی نمی‌کنه
    return 0 if kbps >= 300 else 1


def is_plain(link):
    """بدون TLS یا Reality (Shadowsocks ساده، vmess یا vless بدون tls): فیلتر ایران راحت تشخیصش می‌ده"""
    key = server_key(link)
    if not isinstance(key, tuple):
        return False
    scheme = key[0]
    if scheme == "vmess":
        return key[8] not in ("tls", "reality")
    if scheme in ("trojan", "hysteria2"):
        return key[5] == "none"
    if scheme == "vless":
        return key[5] not in ("tls", "reality", "xtls")
    return True


def flag(cc):
    """پرچم ایموجی از کد دوحرفی کشور (DE → 🇩🇪)"""
    return "".join(chr(0x1F1E6 + ord(c) - 65) for c in cc) if re.fullmatch(r"[A-Z]{2}", cc or "") else ""


def location_of(r):
    """(کشور، شهر) جایی که ترافیک این کانفیگ بیرون می‌آد؛ None اگه معلوم نیست، یا خروجی کلادفلره (مکانش به جای
    کاربر بستگی داره: از گیت‌هاب آمریکا، از ایران اروپا)"""
    if r.status != "ok" or not re.fullmatch(r"[A-Z]{2}", r.country or "") or r.asn == CDN_ASN:
        return None
    return (r.country, r.city or "")


def with_name(link, name):
    """همون کانفیگ با اسم تازه (vmess اسمش داخل خود لینکه، فیلد ps)"""
    body = link.split("#", 1)[0]
    if body.startswith("vmess://"):
        data = body[8:]
        info = json.loads(base64.b64decode(data + "=" * (-len(data) % 4)).decode("utf-8-sig"))
        info["ps"] = name
        return "vmess://" + base64.b64encode(json.dumps(info, ensure_ascii=False).encode("utf-8")).decode()
    return body + "#" + quote(name, safe="")


def backend_key(link):
    """«همون سرور»: کاربر (uuid یا رمز) و دامنه‌ی پشتش (sni یا host)، یا آدرس اگه دامنه نداره. یه سرور که لیست‌ها با چند
    آدرس (IPهای مختلف کلادفلر)، چند path یا fingerprint گذاشتن یکی حساب می‌شه؛ توی یه اجرا ۱۵۹ کانفیگ این‌طوری تکراری بودن.
    Reality: sni فقط اسم سایت پوششیه (بین سرورهای مختلف یکیه)، پس خود آدرس."""
    key = server_key(link)
    if not isinstance(key, tuple):
        return key
    if key[0] == "vmess":
        # ("vmess", add, port, id, net, type, host, path, tls, sni)
        return ("vmess", key[3], key[9] or key[6] or key[1])
    # [scheme, user, host, port] + KEY_PARAMS: type, security, sni, host, ...
    front = key[2] if key[5] == "reality" else (key[6] or key[7] or key[2])
    return (key[0], key[1], front)


def link_name(link):
    """اسم فعلی کانفیگ (vmess: فیلد ps داخل لینک)"""
    body, _, frag = link.partition("#")
    if body.startswith("vmess://"):
        try:
            data = body[8:]
            return str(json.loads(base64.b64decode(data + "=" * (-len(data) % 4)).decode("utf-8-sig")).get("ps") or "")
        except Exception:
            return ""
    return unquote(frag)


def stat_key(link):
    return hashlib.sha1(repr(server_key(link)).encode("utf-8")).hexdigest()[:12]


def load_streaks(old_configs):
    """چند اجرای پشت سر هم هر کانفیگ سالم بوده، تا اجرای قبلی"""
    if STATS_FILE and os.path.exists(STATS_FILE):
        try:
            with open(STATS_FILE, encoding="utf-8") as f:
                return json.load(f)["streak"]
        except Exception as e:
            print(f"   ⚠️ Streaks not loaded: {e}")
    # هنوز آماری نیست: کانفیگ‌های لیست قبلی توی اجرای قبل سالم بودن
    return {stat_key(c): 1 for c in old_configs}


def save_streaks(streaks):
    if not STATS_FILE or DRY_RUN:
        return
    os.makedirs(os.path.dirname(STATS_FILE) or ".", exist_ok=True)
    with open(STATS_FILE, "w", encoding="utf-8") as f:
        json.dump({"streak": streaks}, f)


def load_locations_file():
    """مکان‌های لیست قبلی: {لینک: "DE|Berlin"}"""
    try:
        with open(LOCATIONS_FILE, encoding="utf-8") as f:
            data = json.load(f)
        return data if isinstance(data, dict) else {}
    except Exception:
        return {}


def load_iran_ranges():
    """بازه‌های IP ایران به شکل (شروع، پایان) مرتب، برای جست‌وجوی دودویی"""
    ranges = []
    try:
        with open(IRAN_IP_FILE, encoding="utf-8") as f:
            for line in f:
                line = line.strip()
                if not line or ":" in line:
                    continue
                net = ipaddress.ip_network(line, strict=False)
                ranges.append((int(net.network_address), int(net.broadcast_address)))
    except Exception as e:
        print(f"   ⚠️ Iranian IP ranges not loaded: {e}")
    ranges.sort()
    return ranges


def iran_hosted(link, ranges, starts):
    """سرور توی ایرانه (IP ایران یا دامنه‌ی .ir)؛ از بیرون ایران معلوم نیست کار می‌کنه یا نه"""
    key = server_key(link)
    if not isinstance(key, tuple):
        return False
    host = str(key[1] if key[0] == "vmess" else key[2]).strip("[]").lower()
    if host.endswith(".ir"):
        return True
    try:
        ip = int(ipaddress.IPv4Address(host))
    except ValueError:
        return False
    i = bisect.bisect_right(starts, ip) - 1
    return i >= 0 and ranges[i][0] <= ip <= ranges[i][1]


def dedup_by(links, key):
    out, seen = [], set()
    for link in links:
        k = key(link)
        if k not in seen:
            seen.add(k)
            out.append(link)
    return out


def dedup(links):
    out, seen = [], set()
    for link in links:
        key = server_key(link)
        if key not in seen:
            seen.add(key)
            out.append(link)
    return out


def build_tested_list(candidates, results, old_configs, sources):
    """لیست نهایی (حداکثر MAX_CONFIGS) و مکان هر کانفیگ ({لینک: "DE|Berlin"}).
    ترتیب کیفیت: سرعت کافی، بعد TLS یا Reality (نه ساده)، بعد چند اجرا پشت سر هم سالم مونده، بعد نزدیک به ایران، بعد پینگ.
    از هر شهر چندتای بهتر حتماً هست (انتخاب شهر توی برنامه)، و از هر سرور حداکثر دوتا (زاپاس‌ها با هم نمیرن).
    کانفیگ‌هایی که مکانشون معلومه اسم تازه می‌گیرن، مثلاً «🇩🇪 Berlin».
    None یعنی خیلی کم جواب دادن و احتمالاً خود تست خراب بوده"""
    status = lambda c: results.get(c, UNTESTED)
    exits_iran = [c for c in candidates if status(c).status == "ok" and status(c).country in USELESS_EXITS]
    if exits_iran:
        print(f"   {len(exits_iran)} working configs come out in Iran (useless against the filter); left out")
        for c in exits_iran:
            results[c] = results[c]._replace(status="dead")
    alive = [c for c in candidates if status(c).status == "ok"]

    stats = {}
    for c in candidates:
        name = sources.get(c) or sources.get(c + '#') or "?"
        s = stats.setdefault(name, [0, 0])
        s[0] += 1
        s[1] += status(c).status == "ok"
    print(f"🧪 {len(alive)} of {len(candidates)} configs work. By source (tested / work):")
    for name, (n, ok) in sorted(stats.items(), key=lambda x: -x[1][1]):
        print(f"   {name}: {n} / {ok} ({ok * 100 // max(1, n)}%)")
    if len(alive) < MIN_ALIVE:
        print(f"⚠️ Only {len(alive)} passed (fewer than {MIN_ALIVE}): the test itself may be broken; keeping the untested list")
        return None

    prev = load_streaks(old_configs)
    streak = {c: prev.get(stat_key(c), 0) + 1 for c in alive}
    save_streaks({stat_key(c): n for c, n in streak.items()})

    # مکان: از همین تست؛ اگه این بار پیدا نشد، از لیست قبلی (همون سرور)
    old_locations = {}
    for link, value in load_locations_file().items():
        cc, _, city = str(value).partition("|")
        if cc:
            old_locations[server_key(link)] = (cc, city)

    def loc(c):
        r = status(c)
        return location_of(r) if r.country else old_locations.get(server_key(c))

    def shared_tier(c):
        n = len(SHARED.get(backend_key(c), ()))
        return 0 if n <= 2 else 1 if n <= 4 else 2

    def rank(c):
        r, l, n = status(c), loc(c), streak[c]
        return (speed_rank(r.kbps), is_plain(c), shared_tier(c), 0 if n >= 6 else 1 if n >= 2 else 2,
                l is not None and l[0] in FAR_COUNTRIES, r.ping)

    ranked = sorted(alive, key=rank)
    picked, chosen, backends, per_exit, per_group, per_city, per_country = [], set(), set(), {}, {}, {}, {}
    exit_of = lambda c: status(c).ip or spread_key(c)

    def group_of(c):
        """رنج IP خروجی (/24)؛ خروجی کلادفلر یا نامعلوم: خود سرور"""
        r = status(c)
        if r.ip and r.asn != CDN_ASN:
            try:
                if ipaddress.ip_address(r.ip).version == 4:
                    return "ip:" + ".".join(r.ip.split(".")[:3])
                return "ip6:" + ":".join(r.ip.split(":")[:4])
            except ValueError:
                pass
        return backend_key(c)

    def allowed(c, relaxed=False):
        """از هر سرور واقعی فقط یکی (همیشه)؛ از هر IP خروجی دوتا و هر رنج سه‌تا (مگه وقتی کم اومده)"""
        if c in chosen or backend_key(c) in backends:
            return False
        return relaxed or (per_exit.get(exit_of(c), 0) < PER_EXIT and per_group.get(group_of(c), 0) < PER_GROUP)

    def take(c):
        picked.append(c)
        chosen.add(c)
        backends.add(backend_key(c))
        per_exit[exit_of(c)] = per_exit.get(exit_of(c), 0) + 1
        per_group[group_of(c)] = per_group.get(group_of(c), 0) + 1
        l = loc(c)
        if l:
            per_city[l] = per_city.get(l, 0) + 1
            per_country[l[0]] = per_country.get(l[0], 0) + 1

    # ۱. سهم شهرها، نوبتی: بهترینِ هر شهر، بعد دومیِ هر شهر، ...؛ توی هر نوبت شهرهای کم‌سرور اول (مثلاً برلین با ۶ سرور
    # سالم در کل). شهرهای پرسرور (آمستردام با ۳۰۰) از راه ترتیب کیفیت (پایین) به اندازه‌ی کافی توی لیست میان.
    by_city = {}
    for c in ranked:
        l = loc(c)
        if l:
            by_city.setdefault(l, []).append(c)
    city_order = sorted(by_city.items(), key=lambda x: len(x[1]))   # پایدار: هم‌اندازه‌ها به ترتیب بهترین کانفیگشون
    for turn in range(CITY_RESERVE):
        for l, group in city_order:
            if len(picked) >= MAX_CONFIGS * RESERVE_SHARE:
                break
            # نوبت این شهر: اولین کانفیگ بعدیش که سرورش هنوز سهمش پر نشده
            if per_city.get(l, 0) > turn or per_country.get(l[0], 0) >= COUNTRY_RESERVE:
                continue
            c = next((x for x in group if allowed(x)), None)
            if c:
                take(c)
    # ۲. بقیه به ترتیب کیفیت؛ اگه با سقف IP و رنج کم اومد، بدون اون دوتا (ولی باز هم از هر سرور یکی)
    for relaxed in (False, True):
        for c in ranked:
            if len(picked) >= MAX_CONFIGS:
                break
            if allowed(c, relaxed):
                take(c)
    picked.sort(key=rank)

    # سرورهای داخل ایران که از گیت‌هاب جواب ندادن (شاید فقط از داخل ایران باز باشن) چندتایی ته لیست، و اگه جا موند،
    # اون‌هایی که وقت نشد تست بشن
    ranges = load_iran_ranges()
    starts = [r[0] for r in ranges]
    iran = [c for c in candidates if status(c).status == "dead" and iran_hosted(c, ranges, starts)]
    iran = [c for c in dedup_by(iran, backend_key) if backend_key(c) not in backends][:IRAN_KEEP]
    untested = [c for c in dedup_by(candidates, backend_key) if status(c).status == "untested" and backend_key(c) not in backends]
    final = picked[:MAX_CONFIGS - len(iran)] + iran
    final += untested[:MAX_CONFIGS - len(final)]

    # اسم: مکان («🇩🇪 Berlin»)، یا «Cloudflare» برای خروجی کلادفلر (مکان ثابت نداره)، وگرنه اسم خودش. اسم‌های یکسان
    # شماره می‌گیرن («🇩🇪 Berlin 1»، «🇩🇪 Berlin 2»)، وگرنه ردیف‌ها تکراری به نظر می‌اومدن.
    def base_name(c):
        r = status(c)
        l = loc(c) if r.status == "ok" else None
        if l:
            return f"{flag(l[0])} {l[1] or l[0]}"
        if r.status == "ok" and r.asn == CDN_ASN:
            return "Cloudflare"
        return link_name(c).strip()

    names = [base_name(c) for c in final]
    repeats, seen = Counter(n for n in names if n), {}
    out, locations = [], {}
    for c, name in zip(final, names):
        if name and repeats[name] > 1:
            seen[name] = seen.get(name, 0) + 1
            name = f"{name} {seen[name]}"
        new = with_name(c, name) if name and name != link_name(c) else c
        l = loc(c) if status(c).status == "ok" else None
        if l:
            locations[new] = f"{l[0]}|{l[1]}"
        src = sources.get(c) or sources.get(c + "#")
        if src:
            sources[new] = src
        out.append(new)

    in_list = [c for c in final if status(c).status == "ok"]
    countries, cities = {}, {}
    for c in in_list:
        l = loc(c)
        if l:
            countries[l[0]] = countries.get(l[0], 0) + 1
            cities[l] = cities.get(l, 0) + 1
    all_cities = {}
    for c in alive:
        l = loc(c)
        if l:
            all_cities[l] = all_cities.get(l, 0) + 1
    print(f"   Same server more than once among the working: {len(alive) - len({backend_key(c) for c in alive})} (one kept each)")
    spread_counts = Counter(len(SHARED.get(backend_key(c), ())) for c in alive)
    print("   Working servers by how many channels/lists shared them: " + ", ".join(f"{k}: {v}" for k, v in sorted(spread_counts.items())))
    print(f"   In the list: {len(in_list)} working, {len(iran)} in Iran (untested), {len(final) - len(in_list) - len(iran)} untested; "
          f"plain (no TLS) {sum(map(is_plain, in_list))}, worked 6+ runs in a row {sum(streak[c] >= 6 for c in in_list)}, "
          f"2+ runs {sum(streak[c] >= 2 for c in in_list)}, location known {sum(cities.values())}")
    print(f"   Countries ({len(countries)}): " + ", ".join(f"{k} {v}" for k, v in sorted(countries.items(), key=lambda x: -x[1])))
    print(f"   Cities ({len(cities)} in the list, {len(all_cities)} among all working): " +
          ", ".join(f"{k[1] or '?'} ({k[0]}) {v}/{all_cities.get(k, 0)}" for k, v in sorted(cities.items(), key=lambda x: -x[1])[:60]))
    return out, locations


if __name__ == "__main__":
    # 1. خواندن کانفیگ‌های قدیمی از فایل (برای اینکه همش نپره)
    old_configs = []
    if os.path.exists(OUTPUT_FILE):
        try:
            with open(OUTPUT_FILE, "r", encoding="utf-8") as f:
                for line in f:
                    link = clean_link(line)
                    if link:
                        old_configs.append(link)
        except:
            pass

    # 2. خواندن منبع کانفیگ‌های قدیمی و گرفتن کانفیگ‌های جدید از تلگرام
    sources = {}
    if os.path.exists(SOURCES_FILE):
        try:
            with open(SOURCES_FILE, "r", encoding="utf-8") as f:
                sources = json.load(f)
        except:
            pass
    if not isinstance(sources, dict):
        sources = {}

    new_configs = get_new_configs(sources)

    # اگه از هیچ کانالی هیچی نگرفتیم یعنی یه جای کار خرابه؛
    # با خطا تموم می‌کنیم تا توی GitHub قرمز بشه و بفهمیم (فایل قبلی دست نمی‌خوره)
    if not new_configs:
        print("❌ No configs found from any channel! Check the log above.")
        sys.exit(1)

    # 3. ترکیب هوشمند (جدیدها اول، قدیمی‌ها بعد)
    # یه سرور تکراری حساب می‌شه حتی با اسم دیگه، کانال دیگه، ترتیب پارامتر دیگه یا vmess با اسم داخلی دیگه
    combined = dedup(new_configs + old_configs)
    locations = None   # None: تست اجرا نشد؛ مکان‌های قبلی برای همون لینک‌ها می‌مونن

    # 3.5. با تست: لیست‌های عمومی گیت‌هاب هم اضافه می‌شن و فقط کانفیگ‌هایی که واقعاً کار می‌کنن می‌مونن
    if TESTER and XRAY:
        candidates = dedup(new_configs + old_configs + get_list_configs(sources))[:MAX_CANDIDATES]
        results = test_configs(candidates)
        tested = build_tested_list(candidates, results, old_configs, sources) if results is not None else None
        if tested is not None:
            combined, locations = tested
    if DRY_RUN:
        print(f"🧪 Dry run: {len(combined[:MAX_CONFIGS])} configs would be written; the list is not changed")
        sys.exit(0)

    # 4. اعمال محدودیت (برش زدن لیست)
    if len(combined) > MAX_CONFIGS:
        final_list = combined[:MAX_CONFIGS]
        print(f"✂️ Trimmed list from {len(combined)} to {MAX_CONFIGS}")
    else:
        final_list = combined
        print(f"📦 Total configs: {len(final_list)} ({len(new_configs)} from Telegram this run)")

    # 5. ذخیره نهایی
    if final_list:
        with open(OUTPUT_FILE, "w", encoding="utf-8") as f:
            # زمان ساخت: بعضی آینه‌ها (fastly، gcore، githack) تا مدتی نسخه‌ی قبلی رو می‌دن؛ برنامه از بین آینه‌ها تازه‌ترین رو
            # برمی‌داره. خطی که با # شروع می‌شه کانفیگ نیست و برنامه‌ها (و همین اسکریپت) ازش رد می‌شن.
            f.write(f"# Derafsh {time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())}\n")
            for config in final_list:
                f.write(config + "\n")
        with open(SUB_FILE, "w", encoding="ascii") as f:
            f.write(base64.b64encode("\n".join(final_list).encode("utf-8")).decode("ascii"))
        # فقط منبع کانفیگ‌هایی که توی لیست نهایی هستن رو نگه می‌داریم
        # (c + '#'): کلید قدیمی لینک‌هایی که قبلاً با # خالی ذخیره شده بودن
        final_sources = {c: sources.get(c) or sources[c + '#'] for c in final_list if c in sources or c + '#' in sources}
        with open(SOURCES_FILE, "w", encoding="utf-8") as f:
            json.dump(final_sources, f, ensure_ascii=False, indent=1)
        if locations is None:
            old = load_locations_file()
            locations = {c: old[c] for c in final_list if c in old}
        with open(LOCATIONS_FILE, "w", encoding="utf-8") as f:
            json.dump({c: locations[c] for c in final_list if c in locations}, f, ensure_ascii=False, indent=1)
        print("💾 Update Complete!")
    else:
        print("⚠️ No configs found.")
