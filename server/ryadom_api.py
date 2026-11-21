#!/usr/bin/env python3
"""«Рядом» — выдача кодов и пропусков LiveKit.

Человек («Мне нужна помощь») получает 9-значный код и пропуск в свою комнату;
помощник по коду получает пропуск в ту же комнату. Сам экран и мышь идут
через LiveKit, сюда — только короткие запросы. Только стандартная библиотека.

Ключ LiveKit: файл из LoadCredential=lk (строка «ключ: секрет», как в livekit.yaml).
"""
import base64
import hashlib
import hmac
import json
import os
import secrets
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

PORT = int(os.environ.get("RYADOM_PORT", "3090"))
LK_URLS = os.environ.get("RYADOM_LK_URLS", "wss://lk.94.159.117.75.sslip.io,wss://lk.94.159.117.75.sslip.io:2053").split(",")
CODE_TTL = 24 * 3600          # код живёт сутки
TOKEN_TTL = 12 * 3600         # пропуск — 12 часов (нужен только для входа и переподключений)
JOIN_LIMIT = 20               # неудачных попыток ввода кода с одного адреса за 10 минут
VERSION = os.environ.get("RYADOM_VERSION", "1.0.0")


def load_key():
    path = os.path.join(os.environ.get("CREDENTIALS_DIRECTORY", "/etc/ryadom"), "lk")
    with open(path, encoding="utf-8") as f:
        line = f.read().strip()
    key, secret = line.split(":", 1)
    return key.strip(), secret.strip()


API_KEY, API_SECRET = load_key()
lock = threading.Lock()
codes = {}      # код → {"room", "host_key", "name", "created"}
failures = {}   # адрес → [время неудачи, …]


def b64(data: bytes) -> str:
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode()


def token(identity: str, name: str, room: str, host: bool) -> str:
    now = int(time.time())
    claims = {
        "iss": API_KEY,
        "sub": identity,
        "name": name,
        "nbf": now - 10,
        "exp": now + TOKEN_TTL,
        "video": {
            "room": room,
            "roomJoin": True,
            # Экран публикует только человек; помощник лишь смотрит и шлёт данные (мышь, клавиши).
            "canPublish": host,
            "canPublishSources": ["screen_share"] if host else [],
            "canSubscribe": not host,
            "canPublishData": True,
        },
    }
    head = b64(json.dumps({"alg": "HS256", "typ": "JWT"}, separators=(",", ":")).encode())
    body = b64(json.dumps(claims, separators=(",", ":"), ensure_ascii=False).encode())
    sig = b64(hmac.new(API_SECRET.encode(), f"{head}.{body}".encode(), hashlib.sha256).digest())
    return f"{head}.{body}.{sig}"


def clean_name(value, fallback: str) -> str:
    name = str(value or "").strip()
    name = "".join(ch for ch in name if ch.isprintable())[:40]
    return name or fallback


def expire():
    now = time.time()
    for code in [c for c, v in codes.items() if now - v["created"] > CODE_TTL]:
        del codes[code]
    for ip in list(failures):
        failures[ip] = [t for t in failures[ip] if now - t < 600]
        if not failures[ip]:
            del failures[ip]


def new_code() -> str:
    while True:
        code = f"{secrets.randbelow(900_000_000) + 100_000_000}"
        if code not in codes:
            return code


class Handler(BaseHTTPRequestHandler):
    server_version = "ryadom"
    sys_version = ""

    def log_message(self, fmt, *args):  # без адресов и кодов в журнале
        pass

    def ip(self) -> str:
        return (self.headers.get("X-Forwarded-For") or self.client_address[0]).split(",")[0].strip()

    def reply(self, status: int, data: dict):
        body = json.dumps(data, ensure_ascii=False).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Cache-Control", "no-store")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def body(self) -> dict:
        length = min(int(self.headers.get("Content-Length") or 0), 4096)
        try:
            data = json.loads(self.rfile.read(length) or b"{}")
            return data if isinstance(data, dict) else {}
        except (ValueError, UnicodeDecodeError):
            return {}

    def do_GET(self):
        if self.path == "/api/version":
            return self.reply(200, {"version": VERSION})
        self.reply(404, {"error": "Нет такого адреса"})

    def do_POST(self):
        data = self.body()
        with lock:
            expire()
            if self.path == "/api/host":
                return self.host(data)
            if self.path == "/api/host/renew":
                return self.renew(data)
            if self.path == "/api/join":
                return self.join(data)
        self.reply(404, {"error": "Нет такого адреса"})

    def host(self, data):
        code = new_code()
        room = "r-" + secrets.token_hex(12)
        host_key = secrets.token_urlsafe(24)
        name = clean_name(data.get("name"), "Компьютер")
        codes[code] = {"room": room, "host_key": host_key, "name": name, "created": time.time()}
        self.reply(200, {"code": code, "hostKey": host_key, "urls": LK_URLS,
                         "token": token("host", name, room, host=True)})

    def renew(self, data):
        entry = codes.get(str(data.get("code") or ""))
        if not entry or not hmac.compare_digest(entry["host_key"], str(data.get("hostKey") or "")):
            return self.reply(404, {"error": "Код устарел"})
        entry["created"] = time.time()
        self.reply(200, {"urls": LK_URLS, "token": token("host", entry["name"], entry["room"], host=True)})

    def join(self, data):
        ip = self.ip()
        if len(failures.get(ip, [])) >= JOIN_LIMIT:
            return self.reply(429, {"error": "Слишком много попыток. Подождите 10 минут."})
        code = "".join(ch for ch in str(data.get("code") or "") if ch.isdigit())
        entry = codes.get(code)
        if not entry:
            failures.setdefault(ip, []).append(time.time())
            return self.reply(404, {"error": "Такого кода нет. Проверьте цифры или попросите новый код."})
        name = clean_name(data.get("name"), "Помощник")
        identity = "helper-" + secrets.token_hex(4)
        self.reply(200, {"urls": LK_URLS, "hostName": entry["name"],
                         "token": token(identity, name, entry["room"], host=False)})


if __name__ == "__main__":
    ThreadingHTTPServer.daemon_threads = True
    ThreadingHTTPServer(("127.0.0.1", PORT), Handler).serve_forever()
