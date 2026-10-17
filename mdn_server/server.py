"""MDN Windows service and built-in administration, backed by SQLite."""
from __future__ import annotations
import argparse
import asyncio
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
from fastapi import FastAPI, HTTPException, Request, Form, WebSocket, WebSocketDisconnect
from fastapi.responses import RedirectResponse, Response, JSONResponse, FileResponse
from fastapi.templating import Jinja2Templates
from fastapi.staticfiles import StaticFiles
from pydantic import BaseModel, Field, ConfigDict
from starlette.middleware.sessions import SessionMiddleware
from lesson_distribution import LessonDistribution
from update_state import initialize_updates, assign_update, effective_update, stop_updates, active_ids, update_dashboard

ASSETS = Path(getattr(sys, "_MEIPASS", Path(__file__).parent))
FEATURES = ["playback", "importAudio", "importIlp", "authoring", "asr", "mcp", "export", "importProject", "dataMaintenance", "apiConfiguration", "updates"]
SCHEMA = json.loads((ASSETS / "settings-schema.json").read_text(encoding="utf-8"))
DEFAULT_POLICY = {"settings": {}, "lockedSettings": [], "disabledFeatures": [], "lockAllSettings": False, "lockManagement": False, "hideAsrConfiguration": False, "automaticOobe": False, "update": None}
MDN_LOOP = "asyncio:SelectorEventLoop"


class RealtimeConnections:
    def __init__(self):
        self.peers = {}
        self.locks = {}

    async def send(self, socket, message):
        async with self.locks[socket]:
            await asyncio.wait_for(socket.send_json(message), 5)

    async def notify(self, scope):
        sockets = [socket for identifier, peers in self.peers.items() if scope in ("all", identifier) for socket in peers.copy()]
        await asyncio.gather(*(self.safe_send(socket, {"type": "policy"}) for socket in sockets))

    async def safe_send(self, socket, message):
        try:
            await self.send(socket, message)
        except (RuntimeError, TimeoutError, WebSocketDisconnect, OSError, KeyError):
            try:
                await socket.close(code=1011)
            except (RuntimeError, OSError):
                pass

    async def revoke(self, identifier):
        for socket in self.peers.get(identifier, set()).copy():
            try:
                await socket.close(code=4403)
            except (RuntimeError, OSError):
                pass


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
    for name, bounds in {"cloudTimeoutSeconds": (30, 1800), "cloudConcurrency": (1, 10), "transcriptFontSize": (14, 28), "transcriptBlurIntensity": (1, 8), "applicationFontWeight": (100, 1000)}.items():
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
    fields = {"startup": {"platform", "architecture"}, "online": set(), "asr": {"durationMs", "success", "errorType"}, "crash": {"exceptionType", "component", "fatal", "exitCode", "stack"},
              "error_track": {"reportId", "exceptionType", "component", "fatal", "exitCode", "platform", "architecture", "processId", "message", "details", "recentOperations"}}
    if event.kind not in fields or set(event.metrics) - fields[event.kind]:
        raise HTTPException(422, "遥测字段不在允许范围内。")
    if len(json.dumps(event.metrics).encode("utf-8")) > (8 * 1024 * 1024 if event.kind == "error_track" else 16000):
        raise HTTPException(422, "遥测数据过大。")
    for key, value in event.metrics.items():
        if key == "stack":
            if not isinstance(value, list) or len(value) > 40 or any(not isinstance(x, str) or len(x) > 1000 for x in value):
                raise HTTPException(422, "调用堆栈无效。")
        elif value is not None and (type(value) not in (str, int, bool) or isinstance(value, str) and len(value) > (8 * 1024 * 1024 if event.kind == "error_track" and key in {"message", "details", "recentOperations"} else 200)):
            raise HTTPException(422, "遥测指标无效。")
    return event.metrics


def create_app(root: Path, config: dict, store: SecretStore | None = None, diagnostics: bool = False) -> FastAPI:
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
            CREATE TABLE IF NOT EXISTS update_tasks(id TEXT PRIMARY KEY, scope TEXT, version TEXT, mode TEXT, created REAL, payload BLOB);
            CREATE TABLE IF NOT EXISTS update_receipts(task_id TEXT, device_id TEXT, status TEXT, progress INTEGER, message TEXT, at REAL, PRIMARY KEY(task_id,device_id));
        """)
        conn.execute("INSERT OR IGNORE INTO policies VALUES(?,?,?)", ("all", store.encode(DEFAULT_POLICY), 1))
        if "leave_allowed" not in {r[1] for r in conn.execute("PRAGMA table_info(devices)")}:
            conn.execute("ALTER TABLE devices ADD COLUMN leave_allowed INTEGER NOT NULL DEFAULT 0")
        if "realtime" not in {r[1] for r in conn.execute("PRAGMA table_info(devices)")}:
            conn.execute("ALTER TABLE devices ADD COLUMN realtime INTEGER NOT NULL DEFAULT 0")
        for column, declaration in [("alias", "TEXT NOT NULL DEFAULT ''"), ("latency", "INTEGER"), ("last_poll", "REAL"), ("policy_revision", "INTEGER NOT NULL DEFAULT 0"), ("policy_received", "REAL")]:
            if column not in {r[1] for r in conn.execute("PRAGMA table_info(devices)")}:
                conn.execute(f"ALTER TABLE devices ADD COLUMN {column} {declaration}")
        if not conn.execute("SELECT 1 FROM sqlite_master WHERE type='table' AND name='update_assignments'").fetchone():
            for row in conn.execute("SELECT scope,payload,revision FROM policies").fetchall():
                update = store.decode(row["payload"]).get("update")
                if update:
                    conn.execute("INSERT OR IGNORE INTO update_tasks VALUES(?,?,?,?,?,?)", (update["id"], row["scope"], update["version"], update["mode"], row["revision"] / 1000000, store.encode(update)))
        initialize_updates(conn, store)
    app = FastAPI(title="Intensive Listening MDN", version="2.1.2.3", docs_url=None, redoc_url=None, openapi_url=None)
    app.add_middleware(SessionMiddleware, secret_key=config["session_secret"], https_only=not config.get("disable_ssl", False), same_site="strict", max_age=3600)
    template_directory = expand_web(root)
    templates = Jinja2Templates(directory=str(template_directory))
    app.mount("/assets", StaticFiles(directory=str(template_directory / "assets")), name="assets")
    login_attempts = {}
    connections = RealtimeConnections()
    packages = root / "packages"
    packages.mkdir(exist_ok=True)

    def trace(request, stage, *, started=None, received=None):
        if not diagnostics:
            return
        duration = "" if started is None else f" elapsedMs={(time.perf_counter() - started) * 1000:.1f}"
        transfer = "" if received is None else f" receivedBytes={received}"
        print(f"MDN TRACE {request.scope.get('mdn_trace_id', '-')} {stage}{duration}{transfer}", flush=True)

    @app.exception_handler(HTTPException)
    async def authentication_failure(request, error):
        if error.status_code == 401 and request.url.path.startswith("/admin") or request.url.path == "/login" and error.status_code in (401, 403):
            request.session.clear()
            return RedirectResponse("/?message=login", 303)
        return JSONResponse({"detail": error.detail}, status_code=error.status_code)

    @app.middleware("http")
    async def headers(request, call_next):
        started = time.perf_counter()
        if diagnostics:
            request.scope["mdn_trace_id"] = uuid.uuid4().hex[:8]
            trace(request, f"request {request.method} {request.url.path} contentLength={request.headers.get('content-length', 'unknown')}")
        upload = request.url.path in ("/admin/update/upload", "/admin/lessons/upload")
        limit = 2 * 1024**3 if upload else 128000
        if int(request.headers.get("content-length", "0") or 0) > limit:
            return Response(status_code=413)
        # Bound streamed/chunked bodies too. Never log enrollment or device credentials.
        if request.method == "POST" and not upload and len(await request.body()) > limit:
            return Response(status_code=413)
        if request.method == "POST" and not upload:
            trace(request, "request body ready", started=started)
        try:
            response = await call_next(request)
        except Exception as error:
            trace(request, f"request failed {type(error).__name__}", started=started)
            raise
        if diagnostics:
            response.headers["Server-Timing"] = f"mdn;dur={(time.perf_counter() - started) * 1000:.1f}"
            trace(request, f"response status={response.status_code}", started=started)
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

    @app.websocket("/api/connect")
    async def connect(socket: WebSocket):
        try:
            identifier = device(socket)
        except HTTPException:
            await socket.close(code=4401)
            return
        await socket.accept()
        connections.peers.setdefault(identifier, set()).add(socket)
        connections.locks[socket] = asyncio.Lock()
        with db() as conn:
            conn.execute("UPDATE devices SET realtime=1 WHERE id=?", (identifier,))
        try:
            await connections.send(socket, {"type": "ready"})
            while True:
                text = await asyncio.wait_for(socket.receive_text(), 75)
                if len(text) > 1024 or json.loads(text).get("type") != "heartbeat":
                    await socket.close(code=4400)
                    break
                device(socket)
                with db() as conn:
                    revision = conn.execute("SELECT max(revision) FROM policies WHERE scope IN ('all',?)", (identifier,)).fetchone()[0]
                await connections.send(socket, {"type": "heartbeat", "sentAt": json.loads(text).get("sentAt"), "revision": revision})
        except HTTPException:
            await socket.close(code=4403)
        except (WebSocketDisconnect, RuntimeError, OSError):
            pass
        except (TimeoutError, ValueError, AttributeError):
            await socket.close(code=4400)
        finally:
            connections.peers[identifier].discard(socket)
            if not connections.peers[identifier]:
                del connections.peers[identifier]
            connections.locks.pop(socket, None)

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
            effective_update(conn, identifier, store)
        return {"deviceToken": credential}

    @app.get("/api/info")
    def server_info():
        private = serialization.load_pem_private_key(config["signing_key"].encode(), None)
        return {"organizationName": config["organization_name"], "certificateSha256": config.get("certificate_sha256", ""),
            "updatePublicKey": base64.b64encode(private.public_key().public_bytes(serialization.Encoding.DER, serialization.PublicFormat.SubjectPublicKeyInfo)).decode()}

    @app.post("/api/leave")
    async def leave(request: Request):
        identifier = device(request)
        with db() as conn:
            row = conn.execute("SELECT leave_allowed FROM devices WHERE id=?", (identifier,)).fetchone()
            if row is None or not row[0]:
                raise HTTPException(403, "退出尚未获得管理员授权。")
            conn.execute("DELETE FROM devices WHERE id=?", (identifier,))
            stop_updates(conn, identifier)
            conn.execute("DELETE FROM update_assignments WHERE device_id=?", (identifier,))
            conn.execute("DELETE FROM policies WHERE scope=?", (identifier,))
            conn.execute("INSERT INTO audit VALUES(?,?,?)", (time.time(), "leave", identifier))
        await connections.revoke(identifier)
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
            result["update"] = effective_update(conn, identifier, store)
            result["lessons"] = lesson_distribution.available(identifier)
            return result

    @app.get("/api/policy")
    def policy(request: Request):
        identifier = device(request)
        with db() as conn:
            conn.execute("UPDATE devices SET last_poll=?, realtime=? WHERE id=?", (time.time(), int(bool(connections.peers.get(identifier))), identifier))
        result = policy_for(identifier)
        if "transcriptBlurIntensity" not in request.headers.get("x-il-capabilities", "").split(","):
            result["settings"].pop("transcriptBlurIntensity", None)
            result["lockedSettings"] = [name for name in result.get("lockedSettings", []) if name != "transcriptBlurIntensity"]
        return result

    @app.post("/api/policy/receipt")
    async def policy_receipt(request: Request):
        identifier = device(request)
        body = await request.json()
        revision = body.get("revision")
        expected = policy_for(identifier)["revision"]
        if type(revision) is not int or not 0 <= revision <= expected:
            raise HTTPException(422, "策略版本无效")
        with db() as conn:
            conn.execute("UPDATE devices SET policy_revision=?,policy_received=? WHERE id=? AND policy_revision<=?", (revision, time.time(), identifier, revision))
        return {"accepted": True}

    @app.post("/api/connection")
    async def connection_state(request: Request):
        identifier = device(request)
        body = await request.json()
        latency = body.get("latency")
        if not isinstance(latency, int) or not 0 <= latency <= 60000:
            raise HTTPException(422, "延迟无效")
        with db() as conn:
            conn.execute("UPDATE devices SET latency=? WHERE id=?", (latency, identifier))
        return {"accepted": True}

    @app.post("/api/updates/{task_id}/receipt")
    async def update_receipt(task_id: str, request: Request):
        identifier = device(request)
        body = await request.json()
        status = body.get("status")
        progress = body.get("progress", 0)
        if status not in ("received", "downloading", "ready", "deferred", "installing", "success", "failed", "skipped") or type(progress) is not int or not 0 <= progress <= 100:
            raise HTTPException(422, "更新状态无效")
        with db() as conn:
            task = conn.execute("SELECT * FROM update_tasks WHERE id=?", (task_id,)).fetchone()
            if not task or task["scope"] not in ("all", identifier):
                raise HTTPException(404, "更新任务不存在")
            if not conn.execute("SELECT 1 FROM update_targets WHERE task_id=? AND device_id=?", (task_id, identifier)).fetchone():
                raise HTTPException(404, "更新任务未下发到当前设备")
            previous = conn.execute("SELECT status,progress FROM update_receipts WHERE task_id=? AND device_id=?", (task_id, identifier)).fetchone()
            if previous:
                stages = {"received": 0, "downloading": 1, "ready": 2, "deferred": 3, "installing": 4}
                regressed = status in stages and previous["status"] in stages and stages[status] < stages[previous["status"]]
                if previous["status"] == "success" and status != "success" or regressed or previous["status"] in ("failed", "skipped") and status == "downloading":
                    return {"accepted": True, "stale": True}
                if status == previous["status"] == "downloading":
                    progress = max(progress, previous["progress"])
            conn.execute("INSERT INTO update_receipts VALUES(?,?,?,?,?,?) ON CONFLICT(task_id,device_id) DO UPDATE SET status=excluded.status, progress=excluded.progress, message=excluded.message, at=excluded.at",
                         (task_id, identifier, status, progress, str(body.get("message", ""))[:512], time.time()))
        return {"accepted": True}

    @app.post("/admin/device/alias")
    def device_alias(request: Request, device_id: str = Form(...), alias: str = Form(""), form_token: str = Form(...)):
        csrf(request, form_token)
        if len(alias.strip()) > 80:
            raise HTTPException(422, "别名最多 80 个字符")
        with db() as conn:
            scope_exists(conn, device_id)
            conn.execute("UPDATE devices SET alias=? WHERE id=?", (alias.strip(), device_id))
        return RedirectResponse("/admin?view=devices", 303)

    @app.post("/admin/updates/clear")
    def clear_update_history(request: Request, form_token: str = Form(...)):
        csrf(request, form_token)
        with db() as conn:
            active = active_ids(conn, store)
            for row in conn.execute("SELECT id FROM update_tasks").fetchall():
                if row[0] not in active:
                    conn.execute("DELETE FROM update_receipts WHERE task_id=?", (row[0],))
                    conn.execute("DELETE FROM update_targets WHERE task_id=?", (row[0],))
                    conn.execute("DELETE FROM update_tasks WHERE id=?", (row[0],))
        return RedirectResponse("/admin?view=updates", 303)

    @app.post("/admin/updates/retry")
    async def retry_update(request: Request, task_id: str = Form(...), device_id: str = Form(...), form_token: str = Form(...)):
        csrf(request, form_token)
        with db() as conn:
            row = conn.execute("SELECT payload FROM update_tasks WHERE id=?", (task_id,)).fetchone()
            if not row:
                raise HTTPException(404, "任务不存在")
            update = store.decode(row[0])
        update["id"] = str(uuid.uuid4())
        write_update(device_id, update)
        await connections.notify(device_id)
        return RedirectResponse("/admin?view=updates", 303)

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
            return templates.TemplateResponse(request=request, name="login.html", context={"csrf": request.session["login_csrf"], "organization": config["organization_name"], "login_message": request.query_params.get("message") == "login"})
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
        password_started = time.perf_counter()
        candidate = password_hash(password, config["password_salt"])
        trace(request, "password verification", started=password_started)
        if not hmac.compare_digest(candidate, config["password_hash"]):
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
    def dashboard(request: Request, scope: str = "all", q: str = "", kind: str = "", event_device: str = "", page: int = 1):
        admin(request)
        database_started = time.perf_counter()
        with db() as conn:
            clients = [dict(r) for r in conn.execute("SELECT d.*,MAX(g.revision,COALESCE(p.revision,0)) AS expected_revision FROM devices d JOIN policies g ON g.scope='all' LEFT JOIN policies p ON p.scope=d.id ORDER BY d.last_seen DESC")]
            row = conn.execute("SELECT * FROM policies WHERE scope=?", (scope,)).fetchone()
            filters = []; parameters = []
            if q:
                filters.append("(e.metrics LIKE ? OR e.device_id LIKE ? OR d.name LIKE ? OR d.alias LIKE ? OR e.version LIKE ?)")
                parameters.extend(["%" + q + "%"] * 5)
            if kind:
                filters.append("e.kind=?"); parameters.append(kind)
            if event_device:
                filters.append("e.device_id=?"); parameters.append(event_device)
            where = " WHERE " + " AND ".join(filters) if filters else ""
            event_count = conn.execute("SELECT count(*) FROM events e LEFT JOIN devices d ON d.id=e.device_id" + where, parameters).fetchone()[0]
            events = [dict(r) for r in conn.execute("SELECT e.*, coalesce(nullif(d.alias,''),d.name,e.device_id) AS device_name FROM events e LEFT JOIN devices d ON d.id=e.device_id" + where + " ORDER BY e.rowid DESC LIMIT 100 OFFSET ?", parameters + [max(0, page - 1) * 100])]
            kinds = [r[0] for r in conn.execute("SELECT DISTINCT kind FROM events ORDER BY kind")]
            updates_context = update_dashboard(conn, store, scope)
        trace(request, "dashboard queries", started=database_started)
        policy_started = time.perf_counter()
        payload = store.decode(row["payload"]) if row else {}
        trace(request, "policy decryption", started=policy_started)
        # API keys stay out of HTML; an unchanged sentinel retains their encrypted value.
        for name in ("cloudApiKey",):
            if name in payload.get("settings", {}):
                payload["settings"][name] = "__KEEP_SECRET__"
        current_update = payload.pop("update", None)
        clients = [describe_device(client) for client in clients]
        labels = {"startup": "启动", "online": "在线", "asr": "语音识别", "crash": "崩溃", "error_track": "错误跟踪"}
        for event in events:
            event["label"] = labels.get(event["kind"], event["kind"])
            metric_labels = {"platform": "系统", "architecture": "架构", "durationMs": "耗时（ms）", "success": "识别成功", "errorType": "错误类型", "exceptionType": "异常类型", "component": "组件", "fatal": "致命错误", "exitCode": "退出码", "stack": "调用堆栈", "reportId": "报告 ID", "processId": "进程 ID", "message": "异常消息", "details": "完整异常", "recentOperations": "最近操作"}
            metrics = json.loads(event["metrics"])
            event["fields"] = [(metric_labels.get(key, key), "是" if value is True else "否" if value is False else "\n".join(value) if isinstance(value, list) else str(value)) for key, value in metrics.items()]
            event["happened"] = datetime.fromisoformat(event["happened"]).astimezone().strftime("%Y-%m-%d %H:%M:%S")
        extra = {"q": q, "kind": kind, "event_device": event_device, "page": max(1,page), "event_count": event_count, "kinds": kinds, **updates_context}
        render_started = time.perf_counter()
        lessons = lesson_distribution.dashboard()
        trace(request, "lesson queries", started=render_started)
        render_started = time.perf_counter()
        response = templates.TemplateResponse(request=request, name="admin.html", context={**extra, "organization": config["organization_name"], "clients": clients, "scope": scope, "policy": json.dumps(payload, ensure_ascii=False, indent=2), "payload": payload, "csrf": request.session["csrf"], "features": FEATURES, "settings": SCHEMA, "events": events, "message": request.query_params.get("message", ""), "server_url": config["public_url"], "certificate": config.get("certificate_sha256", ""), "online_count": sum(c["online"] for c in clients), "current_update": current_update, "lesson_tasks": lessons})
        trace(request, "page rendering", started=render_started)
        return response

    def is_online(row):
        return bool(row["enabled"] and (bool(connections.peers.get(row["id"])) if row["realtime"] else time.time() - row["last_seen"] < 90))

    def connection_text(row):
        if connections.peers.get(row["id"]):
            return "实时模式 - " + (str(row["latency"]) if row["latency"] is not None else "--") + "ms"
        stamp = row["last_poll"]
        return "轮询模式 " + (datetime.fromtimestamp(stamp).astimezone().strftime("%H:%M:%S") if stamp else "尚未轮询")

    def describe_device(row):
        client = dict(row)
        client["original_name"] = client["name"]
        client["name"] = client["alias"] or client["name"]
        client["online"] = is_online(client)
        client["seen_text"] = datetime.fromtimestamp(client["last_seen"]).astimezone().strftime("%Y-%m-%d %H:%M:%S")
        client["connection_text"] = connection_text(client)
        received = client["policy_revision"]
        expected = client["expected_revision"]
        client["policy_state"] = "策略已接收" if received >= expected else "等待接收新策略" if received else "等待策略回执"
        client["policy_detail"] = "最近接收：" + datetime.fromtimestamp(client["policy_received"]).astimezone().strftime("%m-%d %H:%M:%S") if client["policy_received"] else "旧版客户端可能不支持策略回执"
        client["policy_current"] = received >= expected
        return client

    @app.get("/admin/status")
    def online_status(request: Request, scope: str = "all", updates: bool = False):
        admin(request)
        with db() as conn:
            clients = [describe_device(row) for row in conn.execute("SELECT d.*,MAX(g.revision,COALESCE(p.revision,0)) AS expected_revision FROM devices d JOIN policies g ON g.scope='all' LEFT JOIN policies p ON p.scope=d.id ORDER BY d.last_seen DESC")]
            result = {"devices": [{"id": row["id"], "name": row["name"], "original_name": row["original_name"], "online": row["online"], "enabled": bool(row["enabled"]), "connection": row["connection_text"], "version": row["version"], "seen_text": row["seen_text"], "policy_state": row["policy_state"], "policy_detail": row["policy_detail"], "policy_current": row["policy_current"]} for row in clients]}
            result["device_rows_html"] = templates.get_template("device-rows.html").render(request=request, clients=clients, csrf=request.session["csrf"])
            if updates:
                scope_exists(conn, scope)
                result["updates_html"] = templates.get_template("update-feedback.html").render(request=request, scope=scope, csrf=request.session["csrf"], **update_dashboard(conn, store, scope))
            return result

    def scope_exists(conn, scope):
        if scope != "all" and not conn.execute("SELECT id FROM devices WHERE id=?", (scope,)).fetchone():
            raise HTTPException(404, "设备不存在。")

    def write_policy(conn, scope, payload):
        revision = time.time_ns() // 1000
        conn.execute("INSERT INTO policies VALUES(?,?,?) ON CONFLICT(scope) DO UPDATE SET payload=excluded.payload, revision=excluded.revision", (scope, store.encode(payload), revision))

    @app.post("/admin/policy")
    async def save_policy(request: Request, scope: str = Form(...), policy_json: str = Form(...), form_token: str = Form(...)):
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
        await connections.notify(scope)
        return RedirectResponse("/admin?scope=" + scope + "&view=policy&message=saved", 303)

    @app.post("/admin/device")
    async def change_device(request: Request, device_id: str = Form(...), action: str = Form(...), form_token: str = Form(...)):
        csrf(request, form_token)
        with db() as conn:
            scope_exists(conn, device_id)
            if action in ("enable", "disable"):
                conn.execute("UPDATE devices SET enabled=? WHERE id=?", (int(action == "enable"), device_id))
            elif action == "reset":
                stop_updates(conn, device_id)
                conn.execute("DELETE FROM update_assignments WHERE device_id=?", (device_id,))
                conn.execute("DELETE FROM devices WHERE id=?", (device_id,))
            elif action in ("allow_leave", "cancel_leave"):
                conn.execute("UPDATE devices SET leave_allowed=? WHERE id=?", (int(action == "allow_leave"), device_id))
            elif action == "inherit":
                write_policy(conn, device_id, {})
                global_update = store.decode(conn.execute("SELECT payload FROM policies WHERE scope='all'").fetchone()[0]).get("update")
                if global_update:
                    assign_update(conn, device_id, global_update)
                else:
                    stop_updates(conn, device_id)
            else:
                raise HTTPException(422, "未知操作。")
            conn.execute("INSERT INTO audit VALUES(?,?,?)", (time.time(), action, device_id))
        if action in ("disable", "reset"):
            await connections.revoke(device_id)
        else:
            await connections.notify(device_id)
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
    async def publish_update(request: Request, scope: str = Form(...), mode: str = Form(...), version: str = Form(...), url: str = Form(...), sha256: str = Form(...), size: int = Form(...), form_token: str = Form(...)):
        csrf(request, form_token)
        import re
        try:
            valid_url(url, allow_http=True)
            if mode not in ("ota", "cold") or not re.fullmatch(r"\d+\.\d+\.\d+(?:\.\d+)?", version) or not re.fullmatch(r"[a-fA-F0-9]{64}", sha256) or not 0 < size <= 2 * 1024**3:
                raise ValueError("更新字段无效。")
        except ValueError as error:
            raise HTTPException(422, str(error)) from None
        write_update(scope, {"id": str(uuid.uuid4()), "mode": mode, "version": version, "url": url, "sha256": sha256.lower(), "size": size})
        await connections.notify(scope)
        return RedirectResponse("/admin?scope=" + scope + "&view=updates&message=update", 303)

    def write_update(scope, update):
        private = serialization.load_pem_private_key(config["signing_key"].encode(), None)
        update["signature"] = base64.b64encode(private.sign(canonical(update), padding.PKCS1v15(), hashes.SHA256())).decode()
        with db() as conn:
            scope_exists(conn, scope)
            row = conn.execute("SELECT payload FROM policies WHERE scope=?", (scope,)).fetchone()
            payload = store.decode(row[0]) if row else {}
            payload["update"] = update
            conn.execute("INSERT INTO update_tasks VALUES(?,?,?,?,?,?)", (update["id"], scope, update["version"], update["mode"], time.time(), store.encode(update)))
            assign_update(conn, scope, update)
            write_policy(conn, scope, payload)
            conn.execute("INSERT INTO audit VALUES(?,?,?)", (time.time(), "update", scope))

    @app.post("/admin/update/upload")
    async def upload_update(request: Request, scope: str, mode: str, version: str, filename: str):
        upload_started = time.perf_counter()
        csrf(request, request.headers.get("x-csrf-token", ""))
        trace(request, "upload authenticated", started=upload_started)
        import re
        if mode not in ("ota", "cold") or not re.fullmatch(r"\d+\.\d+\.\d+(?:\.\d+)?", version) or not filename.lower().endswith(".exe"):
            raise HTTPException(422, "请选择 EXE 安装包并填写有效版本与更新方式。")
        with db() as conn:
            scope_exists(conn, scope)
        trace(request, "upload scope checked", started=upload_started)
        identifier = str(uuid.uuid4())
        part = packages / (identifier + ".part")
        path = packages / (identifier + ".exe")
        size = 0
        digest = hashlib.sha256()
        prefix = b""
        last_reported = 0
        try:
            with part.open("wb") as output:
                trace(request, "upload awaiting body", started=upload_started, received=size)
                async for chunk in request.stream():
                    size += len(chunk)
                    if size > 2 * 1024**3:
                        raise HTTPException(413, "安装包不能超过 2 GiB。")
                    prefix = (prefix + chunk[:2])[:2] if len(prefix) < 2 else prefix
                    digest.update(chunk)
                    await asyncio.to_thread(output.write, chunk)
                    if diagnostics and (last_reported == 0 or size - last_reported >= 4 * 1024 * 1024):
                        trace(request, "upload body", started=upload_started, received=size)
                        last_reported = size
            trace(request, "upload body complete", started=upload_started, received=size)
            if size < 2 or prefix != b"MZ":
                raise HTTPException(422, "安装包不是有效的 Windows EXE。")
            part.replace(path)
            url = config["public_url"].rstrip("/") + "/api/packages/" + identifier
            persist_started = time.perf_counter()
            write_update(scope, {"id": identifier, "mode": mode, "version": version, "url": url, "sha256": digest.hexdigest(), "size": size})
            trace(request, "update persisted", started=persist_started)
        except BaseException as error:
            trace(request, f"upload failed {type(error).__name__}", started=upload_started, received=size)
            path.unlink(missing_ok=True)
            raise
        finally:
            part.unlink(missing_ok=True)
        await connections.notify(scope)
        trace(request, "upload complete", started=upload_started, received=size)
        return {"redirect": "/admin?scope=" + scope + "&view=updates&message=update"}

    @app.get("/api/packages/{identifier}")
    def download_package(identifier: uuid.UUID, request: Request):
        device_id = device(request)
        update = policy_for(device_id).get("update")
        if not update or urlparse(update["url"]).path != "/api/packages/" + str(identifier):
            raise HTTPException(403, "安装包未下发到当前设备。")
        path = packages / (str(identifier) + ".exe")
        if not path.is_file():
            raise HTTPException(404, "安装包不存在。")
        return FileResponse(path, media_type="application/octet-stream", filename="Intensive-Listening-" + update["version"] + "-Setup.exe")

    @app.post("/admin/update/cancel")
    async def cancel_update(request: Request, scope: str = Form(...), form_token: str = Form(...)):
        csrf(request, form_token)
        with db() as conn:
            scope_exists(conn, scope)
            row = conn.execute("SELECT payload FROM policies WHERE scope=?", (scope,)).fetchone()
            payload = store.decode(row[0]) if row else {}
            payload["update"] = None
            stop_updates(conn, scope)
            write_policy(conn, scope, payload)
        await connections.notify(scope)
        return RedirectResponse("/admin?scope=" + scope + "&view=updates", 303)

    lesson_distribution = LessonDistribution(app, root, config, db, csrf, device, scope_exists, connections)
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
    parser.add_argument("--diagnostics", action="store_true", help="记录请求耗时与更新包接收字节数，排查上传挂起")
    args = parser.parse_args()
    args.data = args.data.resolve()
    path = args.data / "config.bin"
    store = SecretStore()
    print("Intensive Listening MDN 2.1.2.3")
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
    uvicorn.run(create_app(args.data, config, store, diagnostics=args.diagnostics), host=public.get("host", "0.0.0.0"), port=port, loop=MDN_LOOP, access_log=False, **ssl_options)


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
