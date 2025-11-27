import requests
import re
import os

# لیست کانال‌ها (اصلاح شده و استاندارد)
# نکته: لینک آخر رو درست کردم چون https و /s/ نداشت
CHANNELS = [
    "https://t.me/s/Daily_Configs",
    "https://t.me/s/V2rayNGn",
    "https://t.me/s/PrivateVPNs",
    "https://t.me/s/DirectVPN",
    "https://t.me/s/mehrosaboran",
    "https://t.me/s/Cook_Vpn", 
]

# فایل خروجی
OUTPUT_FILE = "derafsh_servers.txt"

# سقف تعداد کانفیگ (که سنگین نشه)
MAX_CONFIGS = 200

def get_new_configs():
    found_configs = []
    pattern = r'(vmess|vless|ss|trojan|hysteria2?)://[a-zA-Z0-9\-\._~:/\?#@!$&\'()*+,;=%]+'
    
    print("🚀 Scraping Telegram Channels...")
    for url in CHANNELS:
        try:
            response = requests.get(url, timeout=10)
            if response.status_code == 200:
                matches = re.findall(pattern, response.text)
                for conf in matches:
                    clean = conf.split('<')[0].split('"')[0].strip()
                    if len(clean) > 15:
                        found_configs.append(clean)
                print(f"   ✅ {url.split('/')[-1]}: Found {len(matches)}")
        except:
            print(f"   ❌ Skip: {url}")
            
    return found_configs

if __name__ == "__main__":
    # 1. خواندن کانفیگ‌های قدیمی از فایل (برای اینکه همش نپره)
    old_configs = []
    if os.path.exists(OUTPUT_FILE):
        try:
            with open(OUTPUT_FILE, "r", encoding="utf-8") as f:
                for line in f:
                    if line.strip():
                        old_configs.append(line.strip())
        except:
            pass

    # 2. گرفتن کانفیگ‌های جدید از تلگرام
    new_configs = get_new_configs()
    
    # 3. ترکیب هوشمند (جدیدها اول، قدیمی‌ها بعد)
    # استفاده از dict برای حذف تکراری‌ها با حفظ ترتیب (جدیدها اولویت دارن)
    combined = list(dict.fromkeys(new_configs + old_configs))
    
    # 4. اعمال محدودیت (برش زدن لیست)
    if len(combined) > MAX_CONFIGS:
        final_list = combined[:MAX_CONFIGS]
        print(f"✂️ Trimmed list from {len(combined)} to {MAX_CONFIGS}")
    else:
        final_list = combined
        print(f"📦 Total configs: {len(final_list)}")

    # 5. ذخیره نهایی
    if final_list:
        with open(OUTPUT_FILE, "w", encoding="utf-8") as f:
            for config in final_list:
                f.write(config + "\n")
        print("💾 Update Complete!")
    else:
        print("⚠️ No configs found.")
