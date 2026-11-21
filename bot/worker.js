// ربات تلگرام درفش (@DERAFSH_VPN_bot) روی Cloudflare Workers: همیشه روشن، هر پیام همون لحظه جواب می‌گیره.
//
//   • دکمه‌ها: «معرفی کانال»، «گزارش مشکل»، «دانلود درفش»، «راهنما».
//   • معرفی کانال: لینک توی صف (Durable Object) می‌ره؛ جمع‌آوری (collector.py، هر ۲ ساعت) صف رو از /api/claim می‌گیره،
//     کانفیگ‌های کانال رو تست می‌کنه و نتیجه رو خودش با ربات به فرستنده می‌گه (channel_submissions.py).
//   • گزارش مشکل: پیام و عکس کاربر برای مدیر فرستاده می‌شه؛ مدیر با Reply روی همون پیام، مستقیم به کاربر جواب می‌ده.
//     مدیر کسیه که یه بار «/admin کد» رو فرستاده (کد: ADMIN_CODE).
//
// Secrets: TELEGRAM_BOT_TOKEN، WEBHOOK_SECRET (تلگرام توی هدر می‌فرسته)، API_SECRET (برای جمع‌آوری و /api/setup)، ADMIN_CODE.
import { DurableObject } from "cloudflare:workers";

const REPO = "Mehinator/Derafsh";
const CHANNEL_NAME = /(?:t\.me\/(?:s\/)?|telegram\.me\/|@)([A-Za-z][A-Za-z0-9_]{4,31})(?![A-Za-z0-9_])/g;
const BUTTONS = { channel: "📢 معرفی کانال", report: "🛠 گزارش مشکل", download: "⬇️ دانلود درفش", help: "❓ راهنما" };
const MENU = {
  keyboard: [[{ text: BUTTONS.channel }, { text: BUTTONS.report }], [{ text: BUTTONS.download }, { text: BUTTONS.help }]],
  resize_keyboard: true,
  is_persistent: true,
};
const RECHECK_DAYS = 7, MAX_PENDING = 50, MAX_PER_PERSON = 3, MAX_REPORTS_PER_DAY = 10, REPORT_WINDOW = 15 * 60;

const TEXT = {
  start:
    "سلام! این ربات «درفش» است؛ VPN رایگان و متن‌باز برای ویندوز و اندروید.\n\n" +
    "با دکمه‌های پایین می‌توانید:\n" +
    "• کانالی را که کانفیگ رایگان می‌گذارد معرفی کنید\n" +
    "• مشکلی را که در برنامه دیدید گزارش بدهید\n" +
    "• آخرین نسخه‌ی درفش را بگیرید",
  channelAsk:
    "لینک کانال تلگرامی را که کانفیگ رایگان (vless، vmess، trojan، ss، hysteria2) می‌گذارد بفرستید، مثلاً t.me/name یا ‎@name.\n\n" +
    "حداکثر تا ۲ ساعت بعد کانفیگ‌های اخیرش تست می‌شود. اگر دست‌کم ۵ تا کار کنند، کانال به منبع‌های درفش اضافه می‌شود و نتیجه را همین‌جا می‌گیرید.",
  reportAsk:
    "مشکل را در یک پیام بنویسید. اگر می‌شود این‌ها را هم بگویید:\n" +
    "• نسخه‌ی درفش (بالای صفحه‌ی برنامه نوشته شده)\n" +
    "• اینترنت: همراه اول، ایرانسل، مخابرات، وای‌فای…\n" +
    "• چه اتفاقی افتاد و از کی\n\n" +
    "اسکرین‌شات هم می‌توانید بفرستید.",
  reportThanks: "ممنون! گزارش‌تان رسید و بررسی می‌شود. اگر چیز دیگری هم هست (مثلاً اسکرین‌شات)، همین الان بفرستید.",
  reportLimit: "امروز گزارش زیادی فرستاده‌اید؛ فردا دوباره امتحان کنید.",
  download:
    "آخرین نسخه‌ی درفش (ویندوز ۱۰ و ۱۱، اندروید ۶ به بالا):\n" +
    `https://github.com/${REPO}/releases/latest\n\n` +
    "کانال درفش (خبر نسخه‌های تازه): https://t.me/DerafshVpn\n\n" +
    "آیفون، آیپد و برنامه‌های دیگر (Hiddify، V2Box، v2rayNG): این لینک اشتراک را اضافه کنید\n" +
    `https://cdn.jsdelivr.net/gh/${REPO}@main/derafsh_sub.txt`,
  help:
    "درفش با یک دکمه وصل می‌شود: سرورها را روی همان اینترنتی که الان دارید تست می‌کند و به سریع‌ترین سالم وصل می‌شود. " +
    "اگر سرور قطع شود، خودش به سرور دیگری می‌رود؛ دکمه‌ی «تعویض سرور» هم همین کار را همان لحظه می‌کند.\n\n" +
    "• کشور یا شهر خاصی می‌خواهید؟ در صفحه‌ی اصلی «انتخاب مکان».\n" +
    "• کانفیگ یا لینک اشتراک خودتان؟ «سرورها» ← «افزودن کانفیگ».\n" +
    "• سروری پیدا نشد؟ «سرورها» ← «تست دوباره»؛ اگر باز نشد، «گزارش مشکل» را بزنید.\n\n" +
    `راهنمای کامل: https://github.com/${REPO}#readme`,
  menu: "یکی از دکمه‌های پایین را انتخاب کنید.",
};

async function tg(env, method, params) {
  const res = await fetch(`https://api.telegram.org/bot${env.TELEGRAM_BOT_TOKEN}/${method}`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(params),
  });
  return res.json();
}

const send = (env, chat, text, extra = {}) =>
  tg(env, "sendMessage", { chat_id: chat, text, disable_web_page_preview: true, reply_markup: MENU, ...extra });

// وضعیت کانال‌ها از خود ریپو (جمع‌آوری می‌نویسه): منبع‌های فعلی، قبول‌شده‌ها، ردشده‌ها؛ ۱۰ دقیقه کش
async function channelState() {
  try {
    const res = await fetch(`https://raw.githubusercontent.com/${REPO}/main/derafsh_channels.json`, { cf: { cacheTtl: 600 } });
    if (res.ok) return await res.json();
  } catch {}
  return {};
}

export class BotStore extends DurableObject {
  constructor(ctx, env) {
    super(ctx, env);
    this.sql = ctx.storage.sql;
    this.sql.exec("CREATE TABLE IF NOT EXISTS pending (key TEXT PRIMARY KEY, name TEXT, chats TEXT, sent INTEGER)");
    this.sql.exec("CREATE TABLE IF NOT EXISTS users (chat INTEGER PRIMARY KEY, mode TEXT, mode_at INTEGER, day TEXT, reports INTEGER)");
    this.sql.exec("CREATE TABLE IF NOT EXISTS kv (k TEXT PRIMARY KEY, v TEXT)");
  }
  getKv(k) { return this.sql.exec("SELECT v FROM kv WHERE k = ?", k).toArray()[0]?.v ?? null; }
  setKv(k, v) { this.sql.exec("INSERT INTO kv (k, v) VALUES (?, ?) ON CONFLICT(k) DO UPDATE SET v = excluded.v", k, v); }
  user(chat) {
    return this.sql.exec("SELECT * FROM users WHERE chat = ?", chat).toArray()[0] ?? { chat, mode: null, mode_at: 0, day: "", reports: 0 };
  }
  setMode(chat, mode) {
    const now = Math.floor(Date.now() / 1000);
    this.sql.exec(`INSERT INTO users (chat, mode, mode_at, day, reports) VALUES (?, ?, ?, '', 0)
                   ON CONFLICT(chat) DO UPDATE SET mode = excluded.mode, mode_at = excluded.mode_at`, chat, mode, now);
  }
  // true اگه امروز هنوز جا داره
  countReport(chat) {
    const day = new Date().toISOString().slice(0, 10);
    const u = this.user(chat);
    const n = u.day === day ? u.reports + 1 : 1;
    if (n > MAX_REPORTS_PER_DAY) return false;
    this.sql.exec(`INSERT INTO users (chat, mode, mode_at, day, reports) VALUES (?, NULL, 0, ?, ?)
                   ON CONFLICT(chat) DO UPDATE SET day = excluded.day, reports = excluded.reports`, chat, day, n);
    return true;
  }
  // "queued" | "again" (همین آدم قبلاً فرستاده) | "full"
  addPending(key, name, chat) {
    const row = this.sql.exec("SELECT chats FROM pending WHERE key = ?", key).toArray()[0];
    if (row) {
      const chats = JSON.parse(row.chats);
      if (chats.includes(chat)) return "again";
      chats.push(chat);
      this.sql.exec("UPDATE pending SET chats = ? WHERE key = ?", JSON.stringify(chats), key);
      return "queued";
    }
    const all = this.sql.exec("SELECT chats FROM pending").toArray();
    if (all.length >= MAX_PENDING || all.filter((r) => JSON.parse(r.chats).includes(chat)).length >= MAX_PER_PERSON) return "full";
    this.sql.exec("INSERT INTO pending (key, name, chats, sent) VALUES (?, ?, ?, ?)", key, name, JSON.stringify([chat]), Math.floor(Date.now() / 1000));
    return "queued";
  }
  // جمع‌آوری صف رو برمی‌داره (و از اینجا پاک می‌شه؛ از اون به بعد توی derafsh_channels.json ریپو نگه داشته می‌شه)
  claimPending() {
    const rows = this.sql.exec("SELECT key, name, chats, sent FROM pending").toArray();
    this.sql.exec("DELETE FROM pending");
    return rows.map((r) => ({ key: r.key, name: r.name, chats: JSON.parse(r.chats), sent: r.sent }));
  }
}

async function onMessage(env, store, msg) {
  const chat = msg.chat?.id;
  if (!chat || msg.chat.type !== "private") return;
  const text = (msg.text || msg.caption || "").trim();
  const admin = await store.getKv("admin");
  const now = Math.floor(Date.now() / 1000);

  // مدیر: «/admin کد» یک بار؛ بعد گزارش‌ها برای اون می‌آد و با Reply جواب می‌ده
  if (text.startsWith("/admin")) {
    if (env.ADMIN_CODE && text.split(/\s+/)[1] === env.ADMIN_CODE) {
      await store.setKv("admin", String(chat));
      return send(env, chat, "شما مدیر ربات شدید. گزارش‌ها اینجا می‌آید؛ برای جواب دادن به کاربر روی پیام گزارش Reply بزنید.");
    }
    return send(env, chat, TEXT.menu);
  }
  if (admin && String(chat) === admin && msg.reply_to_message) {
    const m = /#u(-?\d+)/.exec(msg.reply_to_message.text || msg.reply_to_message.caption || "");
    if (m) {
      const r = await tg(env, "sendMessage", { chat_id: Number(m[1]), text: "پاسخ درفش:\n\n" + text, reply_markup: MENU });
      return send(env, chat, r.ok ? "جواب فرستاده شد." : "جواب نرسید (شاید کاربر ربات را بسته است).");
    }
  }

  if (text === "/start" || text.startsWith("/start ")) { await store.setMode(chat, null); return send(env, chat, TEXT.start); }
  if (text === BUTTONS.channel || text === "/channel") { await store.setMode(chat, "channel"); return send(env, chat, TEXT.channelAsk); }
  if (text === BUTTONS.report || text === "/report") { await store.setMode(chat, "report"); return send(env, chat, TEXT.reportAsk); }
  if (text === BUTTONS.download || text === "/download") return send(env, chat, TEXT.download);
  if (text === BUTTONS.help || text === "/help") return send(env, chat, TEXT.help);

  const user = await store.user(chat);
  const inReport = user.mode === "report" && now - user.mode_at < REPORT_WINDOW;
  const names = [...new Set([...text.matchAll(CHANNEL_NAME)].map((m) => m[1]))];

  // لینک کانال (مگه وسط گزارش باشه)
  if (names.length && !inReport) {
    const state = await channelState();
    const sources = new Set([...(state.sources || []), ...Object.keys(state.accepted || {})]);
    const lines = [];
    for (const name of names.slice(0, MAX_PER_PERSON)) {
      const key = name.toLowerCase();
      const rejected = state.rejected?.[key];
      if (key === "derafshvpn" || key.endsWith("bot")) lines.push(`‏@${name} کانال کانفیگ نیست.`);
      else if (sources.has(key)) lines.push(`کانال ‏@${name} از قبل جزو منبع‌های درفش است. ممنون!`);
      else if (rejected && now - (rejected.at || 0) < RECHECK_DAYS * 86400) {
        const when = new Date(rejected.at * 1000).toISOString().slice(0, 10);
        lines.push(`کانال ‏@${name} تاریخ ${when} بررسی شد و کانفیگ سالم کافی نداشت؛ ${RECHECK_DAYS} روز بعد از آن دوباره بفرستید.`);
      } else {
        const r = await store.addPending(key, name, chat);
        lines.push(r === "full" ? `الان صف بررسی پر است؛ ‏@${name} را چند ساعت دیگر دوباره بفرستید.`
                 : r === "again" ? `‏@${name} از قبل در صف بررسی است.`
                 : `دریافت شد: ‏@${name}. حداکثر تا ۲ ساعت دیگر کانفیگ‌هایش تست می‌شود و نتیجه را همین‌جا می‌فرستم.`);
      }
    }
    await store.setMode(chat, null);
    return send(env, chat, lines.join("\n\n"));
  }
  if (user.mode === "channel" && now - user.mode_at < REPORT_WINDOW)
    return send(env, chat, "لینک کانال پیدا نشد. لینک را به شکل t.me/name یا ‎@name بفرستید.");

  // گزارش: بعد از دکمه‌ی «گزارش مشکل» (تا ۱۵ دقیقه، که اسکرین‌شات‌های بعدی هم برسه)
  if (inReport) {
    if (!admin) return send(env, chat, "فعلاً گزارش دریافت نمی‌شود؛ در کانال t.me/DerafshVpn خبر بدهید.");
    if (!(await store.countReport(chat))) return send(env, chat, TEXT.reportLimit);
    const who = [msg.from?.first_name, msg.from?.last_name].filter(Boolean).join(" ") + (msg.from?.username ? ` (@${msg.from.username})` : "");
    const header = `#u${chat} گزارش از ${who || "کاربر"}`;
    if (msg.text) await tg(env, "sendMessage", { chat_id: Number(admin), text: `${header}\n\n${msg.text}`, disable_web_page_preview: true });
    else await tg(env, "copyMessage", { chat_id: Number(admin), from_chat_id: chat, message_id: msg.message_id, caption: `${header}\n\n${msg.caption || ""}`.trim() });
    await store.setMode(chat, "report");   // پنجره‌ی ۱۵ دقیقه از آخرین پیام
    return send(env, chat, TEXT.reportThanks);
  }
  return send(env, chat, TEXT.menu);
}

function authorized(request, env) {
  return env.API_SECRET && request.headers.get("authorization") === `Bearer ${env.API_SECRET}`;
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    const store = env.STORE.get(env.STORE.idFromName("main"));

    if (url.pathname === "/tg" && request.method === "POST") {
      if (request.headers.get("x-telegram-bot-api-secret-token") !== env.WEBHOOK_SECRET) return new Response("no", { status: 403 });
      const update = await request.json();
      try { if (update.message) await onMessage(env, store, update.message); }
      catch (e) { console.log("update failed", e && e.stack || e); }
      return new Response("ok");   // همیشه ۲۰۰، وگرنه تلگرام همون پیام رو بارها دوباره می‌فرسته
    }

    // جمع‌آوری: صف معرفی کانال‌ها
    if (url.pathname === "/api/claim" && request.method === "POST") {
      if (!authorized(request, env)) return new Response("no", { status: 403 });
      return Response.json({ ok: true, pending: await store.claimPending() });
    }

    // راه‌اندازی: وب‌هوک، فرمان‌ها و (اگه بشه) عکس ربات، همه از سمت Cloudflare (از ایران تلگرام در دسترس نیست)
    if (url.pathname === "/api/setup" && request.method === "POST") {
      if (!authorized(request, env)) return new Response("no", { status: 403 });
      const out = {};
      out.webhook = await tg(env, "setWebhook", {
        url: `${url.origin}/tg`, secret_token: env.WEBHOOK_SECRET, allowed_updates: ["message"], drop_pending_updates: false,
      });
      out.commands = await tg(env, "setMyCommands", { commands: [
        { command: "start", description: "شروع و منو" },
        { command: "channel", description: "معرفی کانال کانفیگ" },
        { command: "report", description: "گزارش مشکل" },
        { command: "download", description: "دانلود درفش" },
        { command: "help", description: "راهنما" },
      ] });
      out.menu = await tg(env, "setChatMenuButton", { menu_button: { type: "commands" } });
      const body = await request.json().catch(() => ({}));
      if (body.photo_base64) {
        const form = new FormData();
        form.append("photo", JSON.stringify({ type: "static", photo: "attach://p" }));
        form.append("p", new Blob([Uint8Array.from(atob(body.photo_base64), (c) => c.charCodeAt(0))], { type: "image/jpeg" }), "p.jpg");
        out.photo = await (await fetch(`https://api.telegram.org/bot${env.TELEGRAM_BOT_TOKEN}/setMyProfilePhoto`, { method: "POST", body: form })).json();
      }
      out.info = await tg(env, "getWebhookInfo", {});
      return Response.json(out);
    }
    return new Response("Derafsh bot");
  },
};
