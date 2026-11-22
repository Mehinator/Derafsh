"""گزارش‌های ناشناس برنامه از داخل ایران: کدوم سرور روی کدوم اینترنت (شماره‌ی ASN، مثلاً ۴۴۲۴۴ ایرانسل) کار کرد.

برنامه (CommunityReports.cs) گزارش رو از داخل خود اتصال به bot/worker.js می‌فرسته و اونجا توی D1 کلادفلر
(derafsh-reports) ذخیره می‌شه. اینجا مستقیم با API کلادفلر خونده می‌شه (CF_API_TOKEN، CF_ACCOUNT_ID).

برنامه هر سرور رو با هش کوتاه همون لینکی که دانلود کرده می‌شناسه (NetworkHistory.KeyOf: sha1 متن لینک تا «#»، ۱۶ رقم
hex). جمع‌آوری هر بار اسم کانفیگ‌ها رو عوض می‌کنه و اسم vmess داخل خود لینکه، پس برای لینک‌هایی که این چند روز منتشر
شدن نگه داشته می‌شه هر هش مال کدوم سرور (stat_key) بوده؛ توی همون فایل آمار کش گیت‌هاب (DERAFSH_STATS).

دو کاربرد:
  • tier: ترتیب لیست. سروری که برای کاربرها توی ایران کار کرده جلوتر، اونی که پشت هم جواب نداده عقب‌تر.
  • networks: derafsh_networks.json برای خود برنامه: برای هر اینترنت، سرورهایی که روی همون اینترنت برای بقیه کار
    کردن (good، بهترها اول) و اون‌هایی که پشت هم جواب ندادن (bad). برنامه اول good‌ها رو تست می‌کنه.
"""
import os
import json
import time
import hashlib

import requests

REPORTS_DB = "63b23d8c-762d-4049-9e5e-54cd9fb804d3"
REPORT_DAYS = 2      # گزارش‌های همین‌قدر اخیر حساب می‌شن
KEEP_DAYS = 3        # قدیمی‌ترها از D1 پاک می‌شن؛ هش لینک‌های منتشرشده هم همین‌قدر نگه داشته می‌شه
PAGE = 2000
VIDEO_SPEED = 150    # KB/s، مثل MainPage.VideoSpeed
MAX_GOOD, MAX_BAD, MAX_NETWORKS = 80, 150, 40


def app_key(link):
    """همون NetworkHistory.KeyOf برنامه"""
    return hashlib.sha1(link.split("#", 1)[0].strip().encode("utf-8")).hexdigest()[:16].upper()


def _query(sql, params=()):
    token, account = os.environ.get("CF_API_TOKEN"), os.environ.get("CF_ACCOUNT_ID")
    if not token or not account:
        return None
    r = requests.post(f"https://api.cloudflare.com/client/v4/accounts/{account}/d1/database/{REPORTS_DB}/query",
                      headers={"Authorization": f"Bearer {token}"}, json={"sql": sql, "params": list(params)}, timeout=60)
    body = r.json()
    if not body.get("success"):
        raise RuntimeError(f"D1: {body.get('errors')}")
    return body["result"][0]["results"]


def remember(keys, links, stat_key):
    """هش لینک‌هایی که برنامه‌ها الان دارن (یا از این اجرا می‌گیرن) ← سرورش"""
    now = int(time.time())
    for link in links:
        keys[app_key(link)] = [stat_key(link), now]


def prune(keys):
    cutoff = time.time() - KEEP_DAYS * 86400
    return {k: v for k, v in keys.items() if v[1] >= cutoff}


def read(keys):
    """{stat_key: {asn: [جواب داد, گزارش, جمع سرعت]}} از گزارش‌های REPORT_DAYS روز اخیر؛ بدون دسترسی به D1: {}"""
    since = int(time.time()) - REPORT_DAYS * 86400
    out, reports, results, unknown, last = {}, 0, 0, 0, -1
    try:
        while True:
            rows = _query("SELECT rowid AS id, asn, data FROM reports WHERE at >= ? AND rowid > ? ORDER BY rowid LIMIT ?",
                          (since, last, PAGE))
            if rows is None:
                print("   Reports from the app: no Cloudflare credentials, skipped")
                return {}
            for row in rows:
                last = row["id"]
                reports += 1
                try:
                    items = json.loads(row["data"])
                except ValueError:
                    continue
                asn = int(row["asn"])
                for item in items:
                    results += 1
                    sk = keys.get(item[0], [None])[0]
                    if sk is None:
                        unknown += 1
                        continue
                    s = out.setdefault(sk, {}).setdefault(asn, [0, 0, 0])
                    ok = 1 if item[1] else 0
                    s[0] += ok
                    s[1] += 1
                    s[2] += int(item[2]) if ok else 0
            if len(rows) < PAGE:
                break
    except Exception as e:
        print(f"   ⚠️ Reports from the app not read: {e}")
        return {}
    asns = {a for s in out.values() for a in s}
    print(f"   Reports from the app (last {REPORT_DAYS} days): {reports} reports, {results} results on {len(asns)} networks, "
          f"{results - unknown} matched a published config, {len(out)} servers")
    return out


def cleanup():
    try:
        _query("DELETE FROM reports WHERE at < ?", (int(time.time()) - KEEP_DAYS * 86400,))
    except Exception as e:
        print(f"   ⚠️ Old reports not deleted: {e}")


def tier(stats):
    """۰ = توی ایران با سرعت ویدیو کار کرده، ۱ = کار کرده، ۲ = گزارشی نیست، ۳ = پشت هم جواب نداده"""
    if not stats:
        return 2
    ok = sum(s[0] for s in stats.values())
    n = sum(s[1] for s in stats.values())
    speed = sum(s[2] for s in stats.values())
    if ok >= 2 and ok * 2 >= n:
        return 0 if speed / ok >= VIDEO_SPEED else 1
    if ok == 0 and n >= 3:
        return 3
    return 2


def networks(final_links, stat_key, iran):
    """derafsh_networks.json: {"AS44244": {"good": [هش لینک‌های همین لیست], "bad": [...]}}"""
    per_net = {}
    for link in final_links:
        for asn, (ok, n, speed) in iran.get(stat_key(link), {}).items():
            per_net.setdefault(asn, []).append((app_key(link), ok, n, speed))
    busiest = sorted(per_net, key=lambda a: -sum(x[2] for x in per_net[a]))[:MAX_NETWORKS]
    out = {}
    for asn in busiest:
        rows = per_net[asn]
        good = sorted((x for x in rows if x[1] >= 1 and x[1] * 2 >= x[2]),
                      key=lambda x: (-(2 * x[1] - x[2]), -(x[3] / max(1, x[1]))))
        bad = [x for x in rows if x[1] == 0 and x[2] >= 3]
        if good or bad:
            out[f"AS{asn}"] = {"good": [x[0] for x in good[:MAX_GOOD]], "bad": [x[0] for x in bad[:MAX_BAD]]}
    print(f"   Hints for the app: {len(out)} networks, " + ", ".join(
        f"{k} {len(v['good'])}/{len(v['bad'])}" for k, v in list(out.items())[:10]))
    return out
