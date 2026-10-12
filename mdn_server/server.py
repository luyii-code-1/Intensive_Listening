"""MDN Windows service and built-in administration, backed by SQLite."""
from __future__ import annotations
import argparse
import base64
import ctypes
import getpass
import hashlib
import hmac
import json
import os
from pathlib import Path
import secrets
import sqlite3
import sys
import time
import uuid
from contextlib import contextmanager
from datetime import datetime, timezone, timedelta
import ipaddress
import shutil
import socket
from cryptography import x509
from cryptography.x509.oid import NameOID
from urllib.parse import urlparse

import uvicorn
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import padding, rsa
from fastapi import FastAPI, HTTPException, Request, Form
from fastapi.responses import RedirectResponse, Response
from fastapi.templating import Jinja2Templates
from fastapi.staticfiles import StaticFiles
from pydantic import BaseModel, Field, ConfigDict
from starlette.middleware.sessions import SessionMiddleware

ASSETS = Path(getattr(sys, "_MEIPASS", Path(__file__).parent))
FEATURES = ["playback", "importAudio", "importIlp", "authoring", "asr", "mcp", "export", "importProject", "dataMaintenance", "apiConfiguration", "updates"]
SCHEMA = json.loads((ASSETS / "settings-schema.json").read_text(encoding="utf-8"))
DEFAULT_POLICY = {"settings": {}, "lockedSettings": [], "disabledFeatures": [], "lockAllSettings": False, "lockManagement": False, "hideAsrConfiguration": False, "automaticOobe": False, "update": None}
MDN_LOOP = "asyncio:SelectorEventLoop"


def local_addresses():
    addresses = []
    try:
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as probe:
            probe.connect(("192.0.2.1", 1))
            addresses.append(probe.getsockname()[0])
    except OSError:
        pass
    try:
        addresses.extend(socket.gethostbyname_ex(socket.gethostname())[2])
    except OSError:
        pass
    return list(dict.fromkeys(ip for ip in addresses if not ip.startswith(("127.", "169.254.")) and ip != "0.0.0.0"))


def expand_web(root):
    target = root / "web" / "templates"
    source = ASSETS / "templates"
    version = hashlib.sha256(b"".join(p.read_bytes() for p in sorted(source.rglob("*")) if p.is_file())).hexdigest()
    marker = root / "web" / "bundle-version.txt"
    if not marker.exists() or marker.read_text() != version:
        if target.exists():
            shutil.copytree(target, root / "web" / ("backup-" + str(time.time_ns())))
        shutil.copytree(source, target, dirs_exist_ok=True)
        marker.write_text(version)
    return target


class SecretStore:
    """DPAPI CurrentUser storage. Windows service must retain its original account."""
    def protect(self, data: bytes, decrypt: bool = False) -> bytes:
        if os.name != "nt":
            raise RuntimeError("MDN 服务端的凭据存储需要 Windows。")
        from ctypes import wintypes
        class Blob(ctypes.Structure):
            _fields_ = [("size", wintypes.DWORD), ("data", ctypes.POINTER(ctypes.c_ubyte))]
        buffer = ctypes.create_string_buffer(data)
        source = Blob(len(data), ctypes.cast(buffer, ctypes.POINTER(ctypes.c_ubyte)))
        output = Blob()
        crypt = ctypes.WinDLL("crypt32", use_last_error=True)
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        crypt.CryptProtectData.argtypes = [ctypes.POINTER(Blob), wintypes.LPCWSTR, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(Blob)]
        crypt.CryptUnprotectData.argtypes = [ctypes.POINTER(Blob), ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(Blob)]
        kernel.LocalFree.argtypes = [ctypes.c_void_p]
        call = crypt.CryptUnprotectData if decrypt else crypt.CryptProtectData
        if not call(ctypes.byref(source), None, None, None, None, 1, ctypes.byref(output)):
            raise ctypes.WinError(ctypes.get_last_error())
        try:
            return ctypes.string_at(output.data, output.size)
        finally:
            kernel.LocalFree(output.data)

    def encode(self, value) -> bytes:
        return self.protect(json.dumps(value, ensure_ascii=False).encode())

    def decode(self, value: bytes):
        return json.loads(self.protect(value, True))


def token_hash(value: str) -> str:
    return hashlib.sha256(value.encode()).hexdigest()


def password_hash(value: str, salt: str) -> str:
    return hashlib.pbkdf2_hmac("sha256", value.encode(), bytes.fromhex(salt), 600_000).hex()


def canonical(update: dict) -> bytes:
    return "\n".join(str(update[k]) for k in ("id", "mode", "version", "url", "sha256", "size")).encode()


def valid_url(value: str, allow_http: bool = False):
    url = urlparse(value)
    if url.scheme not in (("http", "https") if allow_http else ("https",)) or not url.netloc or url.username or url.password or url.fragment:
        raise ValueError("地址必须为有效的 HTTP/HTTPS URL。")


def apply_transport(config, public, disable_ssl=False):
    setting = public.get("disable_ssl", False)
    if type(setting) is not bool:
        raise ValueError("server.json 的 disable_ssl 必须为 true 或 false。")
    disabled = disable_ssl or setting
    address = public.get("public_url", config["public_url"])
    valid_url(address, allow_http=True)
    address = urlparse(address)._replace(scheme="http" if disabled else "https").geturl()
    return config | {"disable_ssl": disabled, "public_url": address,
        "certificate_sha256": "" if disabled else config.get("certificate_sha256", "")}


def validate_policy(policy: dict):
    if not isinstance(policy, dict) or set(policy) - set(DEFAULT_POLICY):
        raise ValueError("策略包含未知字段。")
    for key in ("lockAllSettings", "lockManagement", "hideAsrConfiguration", "automaticOobe"):
        if key in policy and type(policy[key]) is not bool:
            raise ValueError(key + " 必须是布尔值。")
    settings = policy.get("settings", {})
    if not isinstance(settings, dict):
        raise ValueError("settings 必须是对象。")
    kinds = {"string": str, "number": int, "boolean": bool, "array": list, "object": dict}
    for key, value in settings.items():
        if key not in SCHEMA or type(value) is not kinds[SCHEMA[key]]:
            raise ValueError("未知设置或字段类型错误：" + key)
    for name, allowed in (("lockedSettings", SCHEMA), ("disabledFeatures", FEATURES)):
        values = policy.get(name, [])
        if not isinstance(values, list) or any(not isinstance(v, str) or v not in allowed for v in values):
            raise ValueError(name + " 包含未知名称。")
    if settings.get("asrProvider", "cloud") not in ("cloud", "local"):
        raise ValueError("asrProvider 无效。")
    if "themeMode" in settings and settings["themeMode"] not in ("system", "light", "dark"):
        raise ValueError("themeMode 无效。")
    for name, bounds in {"cloudTimeoutSeconds": (30, 1800), "cloudConcurrency": (1, 10), "transcriptFontSize": (14, 28), "applicationFontWeight": (100, 1000)}.items():
        if name in settings and not bounds[0] <= settings[name] <= bounds[1]:
            raise ValueError(name + " 超出允许范围。")
    if policy.get("update") is not None:
        raise ValueError("请通过更新发布表单配置签名更新。")


class Register(BaseModel):
    model_config = ConfigDict(extra="forbid")
    deviceId: uuid.UUID
    deviceName: str = Field(min_length=1, max_length=120)
    appVersion: str = Field(max_length=64)
    enrollmentToken: str = Field(min_length=1, max_length=256)


class Telemetry(BaseModel):
    model_config = ConfigDict(extra="forbid")
    id: str = Field(min_length=1, max_length=80)
    kind: str
    occurredAt: datetime
    appVersion: str = Field(max_length=64)
    metrics: dict


def sanitize_metrics(event: Telemetry):
    fields = {"startup": {"platform", "architecture"}, "online": set(), "asr": {"durationMs", "success", "errorType"}, "crash": {"exceptionType", "component", "fatal", "exitCode", "stack"}}
    if event.kind not in fields or set(event.metrics) - fields[event.kind]:
        raise HTTPException(422, "遥测字段不在允许范围内。")
    if len(json.dumps(event.metrics)) > 16000:
        raise HTTPException(422, "遥测数据过大。")
    for key, value in event.metrics.items():
        if key == "stack":
            if not isinstance(value, list) or len(value) > 40 or any(not isinstance(x, str) or len(x) > 1000 for x in value):
                raise HTTPException(422, "调用堆栈无效。")
        elif value is not None and (type(value) not in (str, int, bool) or isinstance(value, str) and len(value) > 200):
            raise HTTPException(422, "遥测指标无效。")
    return event.metrics


def create_app(root: Path, config: dict, store: SecretStore | None = None) -> FastAPI:
    root.mkdir(parents=True, exist_ok=True)
    store = store or SecretStore()
    @contextmanager
    def db():
        connection = sqlite3.connect(root / "mdn.sqlite3", timeout=15)
        connection.row_factory = sqlite3.Row
        try:
            with connection:
                yield connection
        finally:
            connection.close()
    with db() as conn:
        conn.executescript("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS devices(id TEXT PRIMARY KEY, name TEXT NOT NULL, token_hash TEXT NOT NULL UNIQUE, version TEXT, last_seen REAL, enabled INTEGER NOT NULL DEFAULT 1);
            CREATE TABLE IF NOT EXISTS policies(scope TEXT PRIMARY KEY, payload BLOB NOT NULL, revision INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS events(device_id TEXT, event_id TEXT, kind TEXT, happened TEXT, version TEXT, metrics TEXT, PRIMARY KEY(device_id,event_id));
            CREATE TABLE IF NOT EXISTS audit(at REAL, action TEXT, target TEXT);
        """)
        conn.execute("INSERT OR IGNORE INTO policies VALUES(?,?,?)", ("all", store.encode(DEFAULT_POLICY), 1))
        if "leave_allowed" not in {r[1] for r in conn.execute("PRAGMA table_info(devices)")}:
            conn.execute("ALTER TABLE devices ADD COLUMN leave_allowed INTEGER NOT NULL DEFAULT 0")
    app = FastAPI(title="Intensive Listening MDN", docs_url=None, redoc_url=None, openapi_url=None)
    app.add_middleware(SessionMiddleware, secret_key=config["session_secret"], https_only=not config.get("disable_ssl", False), same_site="strict", max_age=3600)
    template_directory = expand_web(root)
    templates = Jinja2Templates(directory=str(template_directory))
    app.mount("/assets", StaticFiles(directory=str(template_directory / "assets")), name="assets")
    login_attempts = {}

    @app.middleware("http")
    async def headers(request, call_next):
        if int(request.headers.get("content-length", "0") or 0) > 128000:
            return Response(status_code=413)
        # Bound streamed/chunked bodies too. Never log enrollment or device credentials.
        if request.method == "POST" and len(await request.body()) > 128000:
            return Response(status_code=413)
        response = await call_next(request)
        response.headers["Cache-Control"] = "no-store"
        response.headers["X-Content-Type-Options"] = "nosniff"
        response.headers["X-Frame-Options"] = "DENY"
        response.headers["Content-Security-Policy"] = "default-src 'self'; style-src 'self' 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'"
        return response

    def admin(request):
        if request.session.get("admin") is not True:
            raise HTTPException(401, "请登录管理后台。")

    def csrf(request, value):
        admin(request)
        if not hmac.compare_digest(request.session.get("csrf", ""), value):
            raise HTTPException(403, "表单令牌无效，请刷新页面。")

    def device(request):
        token = request.headers.get("authorization", "")
        if not token.startswith("Bearer "):
            raise HTTPException(401, "需要设备凭证。")
        with db() as conn:
            row = conn.execute("SELECT * FROM devices WHERE token_hash=?", (token_hash(token[7:]),)).fetchone()
            if row is None or not row["enabled"]:
                raise HTTPException(403, "设备已禁用或凭证无效。")
            conn.execute("UPDATE devices SET last_seen=? WHERE id=?", (time.time(), row["id"]))
            return row["id"]

    @app.post("/api/register")
    def register(payload: Register):
        if not hmac.compare_digest(token_hash(payload.enrollmentToken), token_hash(config["enrollment_token"])):
            raise HTTPException(403, "注册凭证无效。")
        identifier = str(payload.deviceId)
        credential = secrets.token_urlsafe(48)
        with db() as conn:
            if conn.execute("SELECT id FROM devices WHERE id=?", (identifier,)).fetchone():
                raise HTTPException(409, "设备已注册；凭据丢失时请管理员重置设备注册。")
            conn.execute("INSERT INTO devices(id,name,token_hash,version,last_seen) VALUES(?,?,?,?,?)", (identifier, payload.deviceName, token_hash(credential), payload.appVersion, time.time()))
            conn.execute("INSERT INTO audit VALUES(?,?,?)", (time.time(), "register", identifier))
        return {"deviceToken": credential}

    @app.get("/api/info")
    def server_info():
        private = serialization.load_pem_private_key(config["signing_key"].encode(), None)
        return {"organizationName": config["organization_name"], "certificateSha256": config.get("certificate_sha256", ""),
            "updatePublicKey": base64.b64encode(private.public_key().public_bytes(serialization.Encoding.DER, serialization.PublicFormat.SubjectPublicKeyInfo)).decode()}

    @app.post("/api/leave")
    def leave(request: Request):
        identifier = device(request)
        with db() as conn:
            row = conn.execute("SELECT leave_allowed FROM devices WHERE id=?", (identifier,)).fetchone()
            if row is None or not row[0]:
                raise HTTPException(403, "退出尚未获得管理员授权。")
            conn.execute("DELETE FROM devices WHERE id=?", (identifier,))
            conn.execute("DELETE FROM policies WHERE scope=?", (identifier,))
            conn.execute("INSERT INTO audit VALUES(?,?,?)", (time.time(), "leave", identifier))
        return {"accepted": True}

    def policy_for(identifier):
        with db() as conn:
            global_row = conn.execute("SELECT * FROM policies WHERE scope='all'").fetchone()
            result = store.decode(global_row["payload"])
            own = conn.execute("SELECT * FROM policies WHERE scope=?", (identifier,)).fetchone()
            revision = global_row["revision"]
            if own:
                override = store.decode(own["payload"])
                result["settings"].update(override.get("settings", {}))
                for key, value in override.items():
                    if key != "settings":
                        result[key] = value
                revision = max(revision, own["revision"])
            result["revision"] = revision
            return result

    @app.get("/api/policy")
    def policy(request: Request):
        return policy_for(device(request))

    @app.post("/api/telemetry")
    def telemetry(request: Request, event: Telemetry):
        identifier = device(request)
        metrics = sanitize_metrics(event)
        with db() as conn:
            conn.execute("INSERT OR IGNORE INTO events VALUES(?,?,?,?,?,?)", (identifier, event.id, event.kind, event.occurredAt.isoformat(), event.appVersion, json.dumps(metrics, ensure_ascii=False)))
            conn.execute("UPDATE devices SET version=? WHERE id=?", (event.appVersion, identifier))
            # Bound diagnostic storage to the most recent 100000 events.
            conn.execute("DELETE FROM events WHERE rowid IN (SELECT rowid FROM events ORDER BY rowid DESC LIMIT -1 OFFSET 100000)")
        return {"accepted": True}

    @app.get("/")
    def index(request: Request):
        if not request.session.get("admin"):
            request.session.setdefault("login_csrf", secrets.token_urlsafe(24))
            return templates.TemplateResponse(request=request, name="login.html", context={"csrf": request.session["login_csrf"], "organization": config["organization_name"]})
        return RedirectResponse("/admin", 303)

    @app.post("/login")
    def login(request: Request, password: str = Form(...), form_token: str = Form(...)):
        if not hmac.compare_digest(request.session.get("login_csrf", ""), form_token):
            raise HTTPException(403, "请刷新登录页面。")
        address = request.client.host
        failures = login_attempts.get(address, [])
        failures = [stamp for stamp in failures if stamp > time.time() - 300]
        if len(failures) >= 8:
            raise HTTPException(429, "请稍后重试。")
        if not hmac.compare_digest(password_hash(password, config["password_salt"]), config["password_hash"]):
            login_attempts[address] = failures + [time.time()]
            raise HTTPException(401, "密码错误。")
        login_attempts.pop(address, None)
        request.session.clear()
        request.session.update({"admin": True, "csrf": secrets.token_urlsafe(32)})
        return RedirectResponse("/admin", 303)

    @app.post("/logout")
    def logout(request: Request, form_token: str = Form(...)):
        csrf(request, form_token); request.session.clear()
        return RedirectResponse("/", 303)

    @app.get("/admin")
    def dashboard(request: Request, scope: str = "all"):
        admin(request)
        with db() as conn:
            clients = [dict(r) for r in conn.execute("SELECT * FROM devices ORDER BY last_seen DESC")]
            row = conn.execute("SELECT * FROM policies WHERE scope=?", (scope,)).fetchone()
            events = conn.execute("SELECT device_id,kind,happened,version,metrics FROM events ORDER BY rowid DESC LIMIT 100").fetchall()
        payload = store.decode(row["payload"]) if row else {}
        # API keys stay out of HTML; an unchanged sentinel retains their encrypted value.
        for name in ("cloudApiKey",):
            if name in payload.get("settings", {}):
                payload["settings"][name] = "__KEEP_SECRET__"
        current_update = payload.pop("update", None)
        for client in clients:
            client["online"] = bool(client["enabled"] and time.time() - client["last_seen"] < 180)
            client["seen_text"] = datetime.fromtimestamp(client["last_seen"]).astimezone().strftime("%Y-%m-%d %H:%M:%S")
        return templates.TemplateResponse(request=request, name="admin.html", context={"organization": config["organization_name"], "clients": clients, "scope": scope, "policy": json.dumps(payload, ensure_ascii=False, indent=2), "payload": payload, "csrf": request.session["csrf"], "features": FEATURES, "settings": SCHEMA, "events": events, "message": request.query_params.get("message", ""), "server_url": config["public_url"], "certificate": config.get("certificate_sha256", ""), "online_count": sum(c["online"] for c in clients), "current_update": current_update})

    def scope_exists(conn, scope):
        if scope != "all" and not conn.execute("SELECT id FROM devices WHERE id=?", (scope,)).fetchone():
            raise HTTPException(404, "设备不存在。")

    def write_policy(conn, scope, payload):
        revision = time.time_ns() // 1000
        conn.execute("INSERT INTO policies VALUES(?,?,?) ON CONFLICT(scope) DO UPDATE SET payload=excluded.payload, revision=excluded.revision", (scope, store.encode(payload), revision))

    @app.post("/admin/policy")
    def save_policy(request: Request, scope: str = Form(...), policy_json: str = Form(...), form_token: str = Form(...)):
        csrf(request, form_token)
        try:
            payload = json.loads(policy_json)
            with db() as conn:
                scope_exists(conn, scope)
                old_row = conn.execute("SELECT payload FROM policies WHERE scope=?", (scope,)).fetchone()
                old = store.decode(old_row[0]) if old_row else {}
                if isinstance(payload, dict) and isinstance(payload.get("settings"), dict) and payload["settings"].get("cloudApiKey") == "__KEEP_SECRET__":
                    payload["settings"]["cloudApiKey"] = old.get("settings", {}).get("cloudApiKey", "")
                validate_policy(payload)
                global_row = conn.execute("SELECT payload FROM policies WHERE scope='all'").fetchone()
                global_policy = store.decode(global_row[0])
                effective_settings = (global_policy.get("settings", {}) if scope != "all" else {}) | payload.get("settings", {})
                effective_hidden = payload.get("hideAsrConfiguration", global_policy.get("hideAsrConfiguration", False) if scope != "all" else False)
                if effective_hidden and not effective_settings.get("cloudApiKey"):
                    raise ValueError("隐藏配置需要提供或继承 cloudApiKey。")
                if scope == "all":
                    payload = DEFAULT_POLICY | payload
                if old.get("update"):
                    payload["update"] = old["update"]
                write_policy(conn, scope, payload)
                conn.execute("INSERT INTO audit VALUES(?,?,?)", (time.time(), "policy", scope))
        except (ValueError, TypeError) as error:
            raise HTTPException(422, str(error)) from None
        return RedirectResponse("/admin?scope=" + scope + "&view=policy&message=saved", 303)

    @app.post("/admin/device")
    def change_device(request: Request, device_id: str = Form(...), action: str = Form(...), form_token: str = Form(...)):
        csrf(request, form_token)
        with db() as conn:
            scope_exists(conn, device_id)
            if action in ("enable", "disable"):
                conn.execute("UPDATE devices SET enabled=? WHERE id=?", (int(action == "enable"), device_id))
            elif action == "reset":
                conn.execute("DELETE FROM devices WHERE id=?", (device_id,))
            elif action in ("allow_leave", "cancel_leave"):
                conn.execute("UPDATE devices SET leave_allowed=? WHERE id=?", (int(action == "allow_leave"), device_id))
            elif action == "inherit":
                conn.execute("DELETE FROM policies WHERE scope=?", (device_id,))
            else:
                raise HTTPException(422, "未知操作。")
            conn.execute("INSERT INTO audit VALUES(?,?,?)", (time.time(), action, device_id))
        return RedirectResponse("/admin", 303)

    @app.get("/admin/enrollment.yaml")
    def enrollment(request: Request):
        admin(request)
        private = serialization.load_pem_private_key(config["signing_key"].encode(), None)
        public = base64.b64encode(private.public_key().public_bytes(serialization.Encoding.DER, serialization.PublicFormat.SubjectPublicKeyInfo)).decode()
        # JSON quoted strings are valid YAML scalars, including names containing punctuation.
        values = {"schema_version": 1, "organization_name": config["organization_name"], "server_url": config["public_url"], "enrollment_token": config["enrollment_token"], "update_public_key": public, "poll_seconds": 60, "server_certificate_sha256": config.get("certificate_sha256", ""), "disable_ssl": config.get("disable_ssl", False)}
        body = "\n".join(key + ": " + json.dumps(value, ensure_ascii=False) for key, value in values.items()) + "\n"
        return Response(body, media_type="application/yaml", headers={"Content-Disposition": 'attachment; filename="il_mdn_config.yaml"'})

    @app.post("/admin/update")
    def publish_update(request: Request, scope: str = Form(...), mode: str = Form(...), version: str = Form(...), url: str = Form(...), sha256: str = Form(...), size: int = Form(...), form_token: str = Form(...)):
        csrf(request, form_token)
        import re
        try:
            valid_url(url, allow_http=True)
            if mode not in ("ota", "cold") or not re.fullmatch(r"\d+\.\d+\.\d+", version) or not re.fullmatch(r"[a-fA-F0-9]{64}", sha256) or not 0 < size <= 2 * 1024**3:
                raise ValueError("更新字段无效。")
        except ValueError as error:
            raise HTTPException(422, str(error)) from None
        update = {"id": str(uuid.uuid4()), "mode": mode, "version": version, "url": url, "sha256": sha256.lower(), "size": size}
        private = serialization.load_pem_private_key(config["signing_key"].encode(), None)
        update["signature"] = base64.b64encode(private.sign(canonical(update), padding.PKCS1v15(), hashes.SHA256())).decode()
        with db() as conn:
            scope_exists(conn, scope)
            row = conn.execute("SELECT payload FROM policies WHERE scope=?", (scope,)).fetchone()
            payload = store.decode(row[0]) if row else {}
            payload["update"] = update
            write_policy(conn, scope, payload)
            conn.execute("INSERT INTO audit VALUES(?,?,?)", (time.time(), "update", scope))
        return RedirectResponse("/admin?scope=" + scope + "&view=updates&message=update", 303)

    @app.post("/admin/update/cancel")
    def cancel_update(request: Request, scope: str = Form(...), form_token: str = Form(...)):
        csrf(request, form_token)
        with db() as conn:
            scope_exists(conn, scope)
            row = conn.execute("SELECT payload FROM policies WHERE scope=?", (scope,)).fetchone()
            payload = store.decode(row[0]) if row else {}
            payload["update"] = None
            write_policy(conn, scope, payload)
        return RedirectResponse("/admin?scope=" + scope, 303)

    return app


def initialize(root: Path, organization: str, url: str, password: str, store: SecretStore, disable_ssl=False):
    valid_url(url, allow_http=disable_ssl)
    if not organization or len(password) < 12:
        raise ValueError("组织名称不能为空，管理员密码至少 12 位。")
    root.mkdir(parents=True, exist_ok=True)
    config_path = root / "config.bin"
    if config_path.exists():
        raise ValueError("已有配置，初始化不会覆盖。")
    tls = root / "tls"
    tls.mkdir(exist_ok=True)
    hostname = urlparse(url).hostname
    names = [x509.DNSName("localhost")]
    try:
        names.append(x509.IPAddress(ipaddress.ip_address(hostname)))
    except ValueError:
        names.append(x509.DNSName(hostname))
    private = rsa.generate_private_key(public_exponent=65537, key_size=3072)
    subject = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, hostname)])
    now = datetime.now(timezone.utc)
    certificate = (x509.CertificateBuilder().subject_name(subject).issuer_name(subject)
        .public_key(private.public_key()).serial_number(x509.random_serial_number())
        .not_valid_before(now - timedelta(minutes=5)).not_valid_after(now + timedelta(days=3650))
        .add_extension(x509.SubjectAlternativeName(names), critical=False)
        .sign(private, hashes.SHA256()))
    tls_password = secrets.token_urlsafe(48)
    (tls / "server.crt").write_bytes(certificate.public_bytes(serialization.Encoding.PEM))
    (tls / "server.key").write_bytes(private.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8, serialization.BestAvailableEncryption(tls_password.encode())))
    signing = rsa.generate_private_key(public_exponent=65537, key_size=3072)
    salt = secrets.token_hex(16)
    config = {"organization_name": organization, "public_url": url.rstrip("/") + "/",
        "enrollment_token": secrets.token_urlsafe(48), "password_salt": salt,
        "password_hash": password_hash(password, salt), "session_secret": secrets.token_urlsafe(48),
        "signing_key": signing.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8, serialization.NoEncryption()).decode(),
        "tls_password": tls_password, "certificate_sha256": certificate.fingerprint(hashes.SHA256()).hex(),
        "cert": str((tls / "server.crt").resolve()), "key": str((tls / "server.key").resolve()),
        "port": urlparse(url).port or (80 if disable_ssl else 443)}
    shutil.copytree(ASSETS / "templates", root / "web" / "templates", dirs_exist_ok=True)
    (root / "settings-schema.json").write_bytes((ASSETS / "settings-schema.json").read_bytes())
    (root / "server.json").write_text(json.dumps({"public_url": config["public_url"], "host": "0.0.0.0", "port": config["port"], "disable_ssl": disable_ssl}, ensure_ascii=False, indent=2), encoding="utf-8")
    config_path.write_bytes(store.encode(config))
    return config


def main():
    parser = argparse.ArgumentParser(description="MDN 集控服务：运行目录中自动建立 data，控制台持续运行。")
    parser.add_argument("--data", type=Path, default=Path.cwd() / "data")
    parser.add_argument("--init", action="store_true", help="完成首次配置后退出")
    parser.add_argument("--disable_ssl", action="store_true", help="以 HTTP 模式运行，仅用于可信内网")
    args = parser.parse_args()
    args.data = args.data.resolve()
    path = args.data / "config.bin"
    store = SecretStore()
    print("Intensive Listening MDN")
    print("数据目录：" + str(args.data))
    if args.init and path.exists():
        raise ValueError("已有配置，初始化不会覆盖。")
    if not path.exists():
        print("首次运行：设置组织和管理员账号。TLS 证书与设备注册凭证将自动生成。")
        organization = input("组织名称：").strip()
        addresses = local_addresses()
        default_url = ("http://" if args.disable_ssl else "https://") + (addresses[0] if addresses else "127.0.0.1") + ":8443/"
        url = input("客户端访问地址 [" + default_url + "]：").strip() or default_url
        password = getpass.getpass("管理员密码（至少 12 位）：")
        if password != getpass.getpass("再次输入："):
            raise ValueError("两次密码不一致。")
        initialize(args.data, organization, url, password, store, args.disable_ssl)
        print("初始化完成。配置与管理页面已展开到 data。")
        if args.init:
            return
    config = store.decode(path.read_bytes())
    public_path = args.data / "server.json"
    public = json.loads(public_path.read_text(encoding="utf-8-sig")) if public_path.exists() else {}
    config = apply_transport(config, public, args.disable_ssl)
    port = int(public.get("port", config["port"]))
    scheme = "http" if config["disable_ssl"] else "https"
    print("管理后台：" + config["public_url"])
    print("本机访问：" + scheme + "://localhost:" + str(port) + "/")
    for address in local_addresses():
        print("局域网访问：" + scheme + "://" + address + ":" + str(port) + "/")
    if config["disable_ssl"]:
        print("HTTP 模式：连接未加密，请仅用于可信内网；客户端请使用新的注册 YAML 或开启 HTTP 选项。")
    else:
        print("服务器证书 SHA-256：" + config.get("certificate_sha256", ""))
        print("自动证书首次访问可能显示浏览器证书提示；客户端通过注册 YAML 中的证书指纹验证。")
    print("保持此控制台运行；Ctrl+C 停止服务。")
    # Selector avoids Proactor connection_lost callback errors on abandoned TLS connections.
    ssl_options = {} if config["disable_ssl"] else {"ssl_certfile": config["cert"], "ssl_keyfile": config["key"], "ssl_keyfile_password": config.get("tls_password")}
    uvicorn.run(create_app(args.data, config, store), host=public.get("host", "0.0.0.0"), port=port, loop=MDN_LOOP, access_log=False, **ssl_options)


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        print("服务已停止。")
    except Exception as error:
        print("启动失败：" + str(error))
        if sys.stdin.isatty():
            input("按 Enter 退出。")
        raise SystemExit(1)
