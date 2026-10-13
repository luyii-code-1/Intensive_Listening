"""Authenticated ILP distribution and per-device delivery receipts."""
import asyncio
import base64
import hashlib
import json
import re
import time
import uuid
import zipfile
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import padding
from fastapi import HTTPException, Request, Form
from fastapi.responses import FileResponse, RedirectResponse
from pydantic import BaseModel, Field, ConfigDict


def lesson_content(item):
    return ("ilp\n" + "\n".join(str(item[k]) for k in ("id", "packageUuid", "packageVersion", "url", "sha256", "size"))).encode()


def read_manifest(path):
    with zipfile.ZipFile(path) as archive:
        entries = archive.infolist()
        names = [entry.filename for entry in entries]
        if len(names) != len(set(names)) or any(name.startswith(("/", "\\")) or ".." in name.replace("\\", "/").split("/") for name in names):
            raise ValueError("精听包包含重复或非法路径。")
        manifest_entry = archive.getinfo("manifest.json")
        if manifest_entry.file_size > 256 * 1024:
            raise ValueError("精听包清单过大。")
        manifest = json.loads(archive.read(manifest_entry).decode("utf-8-sig"))
        identifier = str(uuid.UUID(manifest["packageUuid"]))
        version = manifest["packageVersion"]
        title = manifest["title"]
        if manifest["formatVersion"] not in (2, 3) or type(version) is not int or not 1 <= version <= 2147483647 or not isinstance(title, str) or not 0 < len(title.strip()) <= 300:
            raise ValueError("精听包标题或版本无效。")
        audio, transcript = manifest["audioPath"], manifest["transcriptPath"]
        if not re.fullmatch(r"audio\.(mp3|m4a|wav)", audio, re.I) or transcript != "transcript.srt":
            raise ValueError("精听包音频或字幕路径无效。")
        if type(manifest["durationMs"]) is not int or manifest["durationMs"] <= 0:
            raise ValueError("精听包时长无效。")
        for name in (audio, transcript):
            entry = archive.getinfo(name)
            if entry.file_size > 2 * 1024**3 or (entry.external_attr >> 16) & 0xf000 == 0xa000:
                raise ValueError("精听包文件过大或包含符号链接。")
            expected = manifest["sha256"][name]
            if not isinstance(expected, str) or not re.fullmatch(r"[a-f0-9]{64}", expected):
                raise ValueError("精听包哈希无效。")
            digest = hashlib.sha256()
            with archive.open(entry) as source:
                for chunk in iter(lambda: source.read(65536), b""):
                    digest.update(chunk)
            if digest.hexdigest() != expected:
                raise ValueError("精听包音频或字幕校验失败。")
        return {"packageUuid": identifier, "packageVersion": version, "title": title.strip()}


class LessonReceipt(BaseModel):
    model_config = ConfigDict(extra="forbid")
    status: str = Field(pattern=r"^(downloading|deferred|imported|skipped|failed)$")
    message: str = Field(default="", max_length=300)


class LessonDistribution:
    def __init__(self, app, root, config, db, csrf, device, scope_exists, connections):
        self.db = db
        self.files = root / "lessons"
        self.files.mkdir(exist_ok=True)
        with db() as conn:
            conn.executescript("""
                CREATE TABLE IF NOT EXISTS lessons(id TEXT PRIMARY KEY, payload TEXT NOT NULL, active INTEGER NOT NULL DEFAULT 1, created REAL NOT NULL);
                CREATE TABLE IF NOT EXISTS lesson_targets(lesson_id TEXT, scope TEXT, PRIMARY KEY(lesson_id,scope));
                CREATE TABLE IF NOT EXISTS lesson_receipts(lesson_id TEXT, device_id TEXT, status TEXT, message TEXT, received REAL, PRIMARY KEY(lesson_id,device_id));
            """)

        @app.post("/admin/lessons/upload")
        async def upload(request: Request, scopes: str, filename: str):
            csrf(request, request.headers.get("x-csrf-token", ""))
            targets = list(dict.fromkeys(scopes.split(",")))
            if not filename.lower().endswith(".ilp") or not targets or len(targets) > 1000 or "all" in targets and len(targets) != 1:
                raise HTTPException(422, "请选择 .ilp 和有效分发范围。")
            with db() as conn:
                for target in targets:
                    scope_exists(conn, target)
            identifier = str(uuid.uuid4())
            part, path = self.files / (identifier + ".part"), self.files / (identifier + ".ilp")
            size, digest = 0, hashlib.sha256()
            try:
                with part.open("wb") as output:
                    async for chunk in request.stream():
                        size += len(chunk)
                        if size > 2 * 1024**3:
                            raise HTTPException(413, "精听包不能超过 2 GiB。")
                        digest.update(chunk)
                        await asyncio.to_thread(output.write, chunk)
                try:
                    metadata = await asyncio.to_thread(read_manifest, part)
                except (ValueError, KeyError, TypeError, zipfile.BadZipFile, RuntimeError) as error:
                    raise HTTPException(422, "精听包无效：" + str(error)[:200]) from None
                item = {"id": identifier, **metadata, "url": config["public_url"].rstrip("/") + "/api/lessons/" + identifier, "sha256": digest.hexdigest(), "size": size}
                private = serialization.load_pem_private_key(config["signing_key"].encode(), None)
                item["signature"] = base64.b64encode(private.sign(lesson_content(item), padding.PKCS1v15(), hashes.SHA256())).decode()
                part.replace(path)
                with db() as conn:
                    conn.execute("INSERT INTO lessons VALUES(?,?,1,?)", (identifier, json.dumps(item, ensure_ascii=False), time.time()))
                    conn.executemany("INSERT INTO lesson_targets VALUES(?,?)", [(identifier, target) for target in targets])
                    conn.execute("INSERT INTO audit VALUES(?,?,?)", (time.time(), "lesson_publish", identifier))
            except BaseException:
                path.unlink(missing_ok=True)
                raise
            finally:
                part.unlink(missing_ok=True)
            for target in targets:
                await connections.notify(target)
            return {"redirect": "/admin?view=lessons&message=lesson"}

        @app.get("/api/lessons/{identifier}")
        def download(identifier: uuid.UUID, request: Request):
            client = device(request)
            item = next((item for item in self.available(client) if item["id"] == str(identifier)), None)
            if item is None:
                raise HTTPException(403, "精听包未下发到当前设备或已撤回。")
            path = self.files / (str(identifier) + ".ilp")
            if not path.is_file():
                raise HTTPException(404, "精听包不存在。")
            return FileResponse(path, media_type="application/octet-stream", filename=str(identifier) + ".ilp")

        @app.post("/api/lessons/{identifier}/receipt")
        def receipt(identifier: uuid.UUID, request: Request, result: LessonReceipt):
            client = device(request)
            with db() as conn:
                if not conn.execute("SELECT 1 FROM lesson_targets WHERE lesson_id=? AND scope IN ('all',?)", (str(identifier), client)).fetchone():
                    raise HTTPException(403, "精听包未下发到当前设备。")
                conn.execute("INSERT INTO lesson_receipts VALUES(?,?,?,?,?) ON CONFLICT(lesson_id,device_id) DO UPDATE SET status=excluded.status,message=excluded.message,received=excluded.received", (str(identifier), client, result.status, result.message, time.time()))
            return {"accepted": True}

        @app.post("/admin/lessons/withdraw")
        async def withdraw(request: Request, lesson_id: str = Form(...), form_token: str = Form(...)):
            csrf(request, form_token)
            with db() as conn:
                if not conn.execute("SELECT 1 FROM lessons WHERE id=?", (lesson_id,)).fetchone():
                    raise HTTPException(404, "精听包任务不存在。")
                conn.execute("UPDATE lessons SET active=0 WHERE id=?", (lesson_id,))
                targets = [row[0] for row in conn.execute("SELECT scope FROM lesson_targets WHERE lesson_id=?", (lesson_id,))]
                conn.execute("INSERT INTO audit VALUES(?,?,?)", (time.time(), "lesson_withdraw", lesson_id))
            for target in targets:
                await connections.notify(target)
            return RedirectResponse("/admin?view=lessons&message=withdraw", 303)

    def available(self, client):
        with self.db() as conn:
            return [json.loads(row[0]) for row in conn.execute("SELECT payload FROM lessons WHERE active=1 AND id IN (SELECT lesson_id FROM lesson_targets WHERE scope IN ('all',?)) ORDER BY created DESC", (client,))]

    def dashboard(self):
        with self.db() as conn:
            result = []
            for row in conn.execute("SELECT * FROM lessons ORDER BY created DESC"):
                item = json.loads(row["payload"])
                item["active"] = bool(row["active"])
                item["targets"] = [target[0] for target in conn.execute("SELECT scope FROM lesson_targets WHERE lesson_id=?", (item["id"],))]
                item["receipts"] = [dict(receipt) for receipt in conn.execute("SELECT d.name,d.id,COALESCE(r.status,'pending') AS status,COALESCE(r.message,'') AS message FROM devices d LEFT JOIN lesson_receipts r ON r.device_id=d.id AND r.lesson_id=? WHERE EXISTS(SELECT 1 FROM lesson_targets t WHERE t.lesson_id=? AND t.scope IN ('all',d.id))", (item["id"], item["id"]))]
                result.append(item)
            return result
