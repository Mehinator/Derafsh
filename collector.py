import requests
import re
import os
import sys
import json
import html
import base64
from itertools import zip_longest
from urllib.parse import quote, unquote, urlsplit

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
]

# فایل خروجی
OUTPUT_FILE = "derafsh_servers.txt"

# فایل منبع: هر کانفیگ به کدوم کانال تلگرام تعلق داره
SOURCES_FILE = "derafsh_sources.json"

# لینک اشتراک (subscription) استاندارد base64، برای v2rayNG / Hiddify / NekoBox روی اندروید
SUB_FILE = "derafsh_sub.txt"

# سقف تعداد کانفیگ (که سنگین نشه)
MAX_CONFIGS = 2000

# فقط پروتکل‌هایی که برنامه درفش می‌تونه باهاشون وصل بشه
# مهم: (?:...) لازمه. با پرانتز معمولی، re.findall فقط اسم پروتکل (مثلاً "vless") رو برمی‌گردوند
# نه کل لینک رو؛ برای همین تا حالا هیچ کانفیگی از تلگرام جمع نمی‌شد
LINK_PATTERN = re.compile(r'(?:vmess|vless|ss|trojan|hysteria2)://[^\s<>"]+')

# کاراکترهایی که Regex برنامه (ConfigService.cs) قبول می‌کنه؛ بقیه percent-encode می‌شن
# تا برنامه دقیقاً همون لینکی رو بخونه که اینجا ذخیره شده
APP_SAFE_CHARS = set("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._~:/?#@!$&'()*+,;=%")

HEADERS = {"User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36"}


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
    body, sep, name = link.partition('#')
    try:
        if body.startswith("vmess://"):
            # برنامه فقط base64 استاندارد (با = در آخر) رو می‌فهمه
            data = body[8:].replace('-', '+').replace('_', '/')
            text = base64.b64decode(data + '=' * (-len(data) % 4)).decode('utf-8-sig')
            info = json.loads(text)
            if not info.get("add") or not info.get("port"):
                return None
            body = "vmess://" + base64.b64encode(text.encode('utf-8')).decode()
        else:
            body = ''.join(c if c in APP_SAFE_CHARS else quote(c) for c in body)
            parts = urlsplit(body)
            if not parts.hostname or parts.port is None:
                return None
    except Exception:
        return None
    if sep:
        # اسم کانفیگ (بعد از #) هم percent-encode می‌شه؛ برنامه موقع نمایش decode می‌کنه
        name = quote(unquote(name), safe='')
        if not name:
            sep = ''  # لینکی که با # خالی تموم شده
    return body + sep + name


def get_new_configs(sources):
    per_channel = []

    print("🚀 Scraping Telegram Channels...")
    for url in CHANNELS:
        name = url.rstrip('/').split('/')[-1]
        try:
            response = requests.get(url, timeout=15, headers=HEADERS)
            if response.status_code != 200:
                print(f"   ❌ {name}: HTTP {response.status_code}")
                continue

            # تلگرام & رو توی HTML به شکل &amp; می‌نویسه؛ اول برش می‌گردونیم
            page = html.unescape(response.text)
            if "tgme_widget_message" not in page:
                print(f"   ⚠️ {name}: No public messages (channel deleted, private, or preview disabled?)")
                continue

            links = [c for c in (clean_link(m) for m in LINK_PATTERN.findall(page)) if c]
            # پیام‌های جدیدتر پایین صفحه‌ان؛ برعکس می‌کنیم که جدیدترها اول باشن (و تکراری‌ها حذف)
            links = list(dict.fromkeys(reversed(links)))
            for link in links:
                sources[link] = name
            per_channel.append(links)
            print(f"   ✅ {name}: Found {len(links)}")
        except Exception as e:
            print(f"   ❌ {name}: {e}")

    # از هر کانال یکی به نوبت، تا اول لیست فقط مال یه کانال نباشه
    found_configs = []
    for group in zip_longest(*per_channel):
        found_configs.extend(link for link in group if link)
    return found_configs


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
    # یک سرور با اسم‌های (#...) مختلف هم تکراری حساب می‌شه
    combined = []
    seen = set()
    for config in new_configs + old_configs:
        key = config.split('#')[0]
        if key not in seen:
            seen.add(key)
            combined.append(config)

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
            for config in final_list:
                f.write(config + "\n")
        with open(SUB_FILE, "w", encoding="ascii") as f:
            f.write(base64.b64encode("\n".join(final_list).encode("utf-8")).decode("ascii"))
        # فقط منبع کانفیگ‌هایی که توی لیست نهایی هستن رو نگه می‌داریم
        # (c + '#'): کلید قدیمی لینک‌هایی که قبلاً با # خالی ذخیره شده بودن
        final_sources = {c: sources.get(c) or sources[c + '#'] for c in final_list if c in sources or c + '#' in sources}
        with open(SOURCES_FILE, "w", encoding="utf-8") as f:
            json.dump(final_sources, f, ensure_ascii=False, indent=1)
        print("💾 Update Complete!")
    else:
        print("⚠️ No configs found.")
