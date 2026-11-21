"""معرفی کانال با ربات تلگرام درفش (@DERAFSH_VPN_bot).

کاربر لینک یه کانال تلگرام رو برای ربات می‌فرسته. هر اجرای جمع‌آوری (هر ۲ ساعت) پیام‌های تازه‌ی ربات رو می‌خونه، کانفیگ‌های
پیام‌های اخیر اون کانال رو کنار بقیه تست می‌کنه، و اگه دست‌کم ACCEPT_MIN تاشون واقعاً کار کردن، کانال از اون به بعد جزو
منبع‌هاست. کانال قبول‌شده‌ای که REMOVE_AFTER اجرای پشت سر هم (حدود یه روز) هیچ کانفیگ سالمی نداشته باشه دوباره کنار می‌ره.
ربات نتیجه رو به همون کسی که کانال رو فرستاده می‌گه.

وضعیت (کانال‌های قبول‌شده، ردشده، در صف، و آخرین پیامی که خونده شد) توی derafsh_channels.json توی ریپو می‌مونه.
پیام‌ها با offset خونده می‌شن: تلگرام پیامی رو که با offset بالاتر تأیید نشده ۲۴ ساعت نگه می‌داره، پس اگه یه اجرا وسط
کار خراب بشه، اجرای بعدی همون پیام‌ها رو دوباره می‌گیره.
"""
import json
import os
import re
import time
import urllib.parse
import urllib.request

STATE_FILE = "derafsh_channels.json"
ACCEPT_MIN = 5        # کانفیگ سالم از پیام‌های اخیر کانال، برای قبول شدن
REMOVE_AFTER = 12     # اجرای پشت سر هم بدون کانفیگ سالم (حدود یه روز) → کانال کنار می‌ره
RECHECK_DAYS = 7      # کانال ردشده بعد از این‌قدر روز دوباره قابل معرفیه
MAX_PER_RUN = 10      # کانال تازه در هر اجرا؛ بقیه برای اجرای بعد توی صف می‌مونن
MAX_PER_PERSON = 3    # از هر نفر در هر اجرا

# t.me/name ، t.me/s/name ، telegram.me/name یا @name (اسم کانال‌های تلگرام: ۵ تا ۳۲ حرف، اولش حرف لاتین)
CHANNEL_NAME = re.compile(r"(?:t\.me/(?:s/)?|telegram\.me/|@)([A-Za-z][A-Za-z0-9_]{4,31})(?![A-Za-z0-9_])")

HELP = ("سلام! لینک یه کانال تلگرام رو که کانفیگ رایگان (vless، vmess، trojan، ss، hysteria2) می‌ذاره بفرستید، "
        "مثلاً t.me/name یا ‎@name.\n\n"
        "حداکثر تا ۲ ساعت بعد کانفیگ‌های اخیرش تست می‌شه. اگه دست‌کم " + str(ACCEPT_MIN) +
        " تاش کار کنه، کانال به منبع‌های درفش اضافه می‌شه و نتیجه رو همین‌جا می‌گیرید.\n\n"
        "دانلود درفش: github.com/Mehinator/Derafsh/releases/latest")


def _api(method, **params):
    token = os.environ.get("TELEGRAM_BOT_TOKEN")
    data = urllib.parse.urlencode(params).encode()
    with urllib.request.urlopen(f"https://api.telegram.org/bot{token}/{method}", data, timeout=20) as response:
        return json.load(response)


def _reply(chat_id, text):
    try:
        _api("sendMessage", chat_id=chat_id, text=text, disable_web_page_preview="true")
    except Exception as e:
        print(f"   ⚠️ bot reply to {chat_id} failed: {e}")


def load_state():
    state = {"offset": 0, "accepted": {}, "rejected": {}, "pending": {}, "sources": []}
    try:
        with open(STATE_FILE, encoding="utf-8") as f:
            saved = json.load(f)
        if isinstance(saved, dict):
            state.update({k: v for k, v in saved.items() if k in state})
    except (OSError, ValueError):
        pass
    return state


def save_state(state):
    with open(STATE_FILE, "w", encoding="utf-8") as f:
        json.dump(state, f, ensure_ascii=False, indent=1, sort_keys=True)
        f.write("\n")


def accepted_urls(state):
    return [f"https://t.me/s/{info['name']}" for info in state["accepted"].values()]


def read_submissions(state, known):
    """کانال‌های معرفی‌شده به صف (state["pending"]) می‌رن. known: اسم کانال‌هایی که الان منبع‌ان (حروف کوچک).
    با BOT_API_URL: از ربات روی Cloudflare (bot/worker.js، همون لحظه به کاربر جواب داده)؛ وگرنه مستقیم با getUpdates."""
    if os.environ.get("BOT_API_URL"):
        return _claim_from_worker(state, known)
    if not os.environ.get("TELEGRAM_BOT_TOKEN"):
        return
    try:
        result = _api("getUpdates", offset=state["offset"], timeout=0, allowed_updates=json.dumps(["message"]))
    except Exception as e:
        print(f"⚠️ Channel submissions: couldn't read the bot's messages: {e}")
        return
    per_person = {}
    for update in result.get("result", []):
        state["offset"] = max(state["offset"], update["update_id"] + 1)
        message = update.get("message") or {}
        chat = message.get("chat") or {}
        if chat.get("type") != "private":
            continue
        chat_id = chat["id"]
        text = (message.get("text") or "") + " " + (message.get("caption") or "")
        names = list(dict.fromkeys(CHANNEL_NAME.findall(text)))
        if not names:
            _reply(chat_id, HELP)
            continue
        for name in names:
            key = name.lower()
            if key == "derafshvpn" or key.endswith("bot"):
                _reply(chat_id, f"‏@{name} کانال کانفیگ نیست؛ لینک یه کانال تلگرام که کانفیگ می‌ذاره رو بفرستید.")
            elif key in known:
                _reply(chat_id, f"کانال ‏@{name} از قبل جزو منبع‌های درفش است. ممنون!")
            elif key in state["pending"]:
                state["pending"][key]["chats"] = sorted(set(state["pending"][key]["chats"]) | {chat_id})
            elif key in state["rejected"] and time.time() - state["rejected"][key].get("at", 0) < RECHECK_DAYS * 86400:
                when = time.strftime("%Y-%m-%d", time.gmtime(state["rejected"][key]["at"]))
                _reply(chat_id, f"کانال ‏@{name} تاریخ {when} بررسی شد و کانفیگ سالم کافی نداشت. "
                                f"{RECHECK_DAYS} روز بعد از اون می‌تونید دوباره بفرستید.")
            elif per_person.get(chat_id, 0) >= MAX_PER_PERSON or len(state["pending"]) >= MAX_PER_RUN:
                _reply(chat_id, f"الان صف بررسی پره؛ ‏@{name} رو چند ساعت دیگه دوباره بفرستید.")
            else:
                per_person[chat_id] = per_person.get(chat_id, 0) + 1
                state["pending"][key] = {"name": name, "chats": [chat_id], "sent": int(time.time())}
                print(f"   📨 channel submitted: @{name}")


def _claim_from_worker(state, known):
    request = urllib.request.Request(os.environ["BOT_API_URL"].rstrip("/") + "/api/claim", data=b"{}", method="POST",
                                     headers={"Authorization": "Bearer " + os.environ.get("BOT_API_SECRET", ""),
                                              "content-type": "application/json", "User-Agent": "derafsh-collector"})
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            items = json.load(response).get("pending", [])
    except Exception as e:
        print(f"⚠️ Channel submissions: couldn't get the queue from the bot: {e}")
        return
    for item in items:
        key, name, chats = item["key"], item["name"], item["chats"]
        if key in known:
            for chat_id in chats:
                _reply(chat_id, f"کانال ‏@{name} از قبل جزو منبع‌های درفش است. ممنون!")
        elif key in state["pending"]:
            state["pending"][key]["chats"] = sorted(set(state["pending"][key]["chats"]) | set(chats))
        else:
            state["pending"][key] = {"name": name, "chats": chats, "sent": item.get("sent") or int(time.time())}
            print(f"   📨 channel submitted: @{name}")


def judge(state, links_by_channel, is_ok):
    """کانال‌های در صف رو با نتیجه‌ی تست همین اجرا قبول یا رد می‌کنه و به فرستنده‌ها خبر می‌ده.
    links_by_channel: {اسم کانال با حروف کوچک: لینک‌هاش، یا None اگه کانال پیام عمومی نداشت}
    is_ok(link): همین سرور توی تست این اجرا کار کرد (یه سرور با لینک دیگه، از کانال دیگه، فقط یه بار تست می‌شه)"""
    for key, item in list(state["pending"].items()):
        if key not in links_by_channel:
            continue   # این بار گرفته نشد (خطای گذرا)؛ توی صف می‌مونه
        name, links = item["name"], links_by_channel[key]
        if links is None:
            ok, tested = 0, 0
            text = (f"کانال ‏@{name} پیدا نشد یا پیام‌هاش عمومی نیست (پیش‌نمایش وب کانال بسته است)، پس قابل بررسی نبود.")
        else:
            tested = len(links)
            ok = sum(1 for link in links if is_ok(link))
            if ok >= ACCEPT_MIN:
                state["accepted"][key] = {"name": name, "added": time.strftime("%Y-%m-%d", time.gmtime()), "fails": 0,
                                          "ok_at_check": ok}
                state["rejected"].pop(key, None)
                text = (f"کانال ‏@{name} قبول شد! از {tested} کانفیگ اخیرش {ok} تا کار کرد. از این به بعد کانفیگ‌هاش "
                        f"تست می‌شن و سالم‌هاش به لیست درفش اضافه می‌شن. ممنون که معرفی کردید.")
            else:
                text = (f"کانال ‏@{name} بررسی شد: از {tested} کانفیگ اخیرش {ok} تا کار کرد (دست‌کم {ACCEPT_MIN} تا لازمه)، "
                        f"پس فعلاً اضافه نشد. {RECHECK_DAYS} روز دیگه می‌تونید دوباره بفرستید.")
        if key not in state["accepted"]:
            state["rejected"][key] = {"name": name, "at": int(time.time()), "ok": ok, "tested": tested}
        print(f"   📨 @{name}: {ok} of {tested} work → {'accepted' if key in state['accepted'] else 'rejected'}")
        for chat_id in item["chats"]:
            _reply(chat_id, text)
        del state["pending"][key]


def check_health(state, links_by_channel, is_ok):
    """کانال قبول‌شده‌ای که REMOVE_AFTER اجرای پشت سر هم هیچ کانفیگ سالمی نداشت کنار می‌ره."""
    for key, info in list(state["accepted"].items()):
        links = links_by_channel.get(key) or []
        if any(is_ok(link) for link in links):
            info["fails"] = 0
            continue
        info["fails"] = info.get("fails", 0) + 1
        if info["fails"] >= REMOVE_AFTER:
            print(f"   📨 @{info['name']}: no working config for {REMOVE_AFTER} runs → removed from the sources")
            del state["accepted"][key]
            state["rejected"][key] = {"name": info["name"], "at": int(time.time()), "ok": 0, "tested": len(links),
                                      "removed": True}
