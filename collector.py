import requests
import re
import os

# لیست کانال‌های هدف (نسخه وب s/)
# می‌تونی هر کانالی که خواستی اضافه کنی
CHANNELS = [
    "https://t.me/s/Daily_Configs",
    "https://t.me/s/V2rayNGn",
    "https://t.me/s/PrivateVPNs",
    "https://t.me/s/DirectVPN",
    "https://t.me/s/mehrosaboran",
    "t.me/Cook_Vpn",
]

# فایل خروجی
OUTPUT_FILE = "derafsh_servers.txt"

def get_configs():
    valid_configs = set() # استفاده از set برای حذف تکراری‌ها
    
    # الگوی Regex برای شکار لینک‌ها
    pattern = r'(vmess|vless|ss|trojan|hysteria2?)://[a-zA-Z0-9\-\._~:/\?#@!$&\'()*+,;=%]+'
    
    print("🚀 Starting Config Collection...")

    for url in CHANNELS:
        try:
            print(f"🔎 Scanning: {url}")
            response = requests.get(url, timeout=15)
            if response.status_code == 200:
                # پیدا کردن همه لینک‌ها
                matches = re.findall(pattern, response.text)
                for conf in matches:
                    # تمیزکاری (حذف تگ‌های HTML که ممکنه چسبیده باشه)
                    clean_conf = conf.split('<')[0].split('"')[0].strip()
                    # فیلتر کردن لینک‌های خیلی کوتاه یا خراب
                    if len(clean_conf) > 15:
                        valid_configs.add(clean_conf)
                print(f"   ✅ Found {len(matches)} links.")
        except Exception as e:
            print(f"   ❌ Error: {e}")

    return list(valid_configs)

if __name__ == "__main__":
    configs = get_configs()
    
    if configs:
        # ذخیره در فایل
        with open(OUTPUT_FILE, "w", encoding="utf-8") as f:
            # نوشتن هدر (اختیاری)
            # f.write(f"# Derafsh Auto-Update: {len(configs)} servers\n")
            for config in configs:
                f.write(config + "\n")
        
        print(f"\n🎉 Success! Saved {len(configs)} unique configs to {OUTPUT_FILE}")
    else:
        print("\n⚠️ No configs found! Check internet or channels.")
        # اگه چیزی پیدا نکرد، فایل رو خالی نکنیم که قبلی‌ها بپرن (اختیاری)
        # exit(1)
