"""Managed cloud policy, encrypted terminal snapshots, and session-bound logout commands."""
from __future__ import annotations

import time
from datetime import datetime
from typing import Literal
from uuid import UUID

from fastapi import HTTPException
from pydantic import BaseModel, ConfigDict, Field, field_validator, model_validator


CLOUD_DEFAULT = {"mode": "followLocal", "requireLogin": False, "lockAccount": False}


def validate_cloud_policy(value):
    if not isinstance(value, dict) or set(value) - set(CLOUD_DEFAULT):
        raise ValueError("cloudService 包含未知字段或类型错误。")
    if "mode" in value and value["mode"] not in ("followLocal", "enabled", "disabled"):
        raise ValueError("cloudService.mode 必须为 followLocal、enabled 或 disabled。")
    for key in ("requireLogin", "lockAccount"):
        if key in value and type(value[key]) is not bool:
            raise ValueError("cloudService." + key + " 必须是布尔值。")


def cloud_policy(global_policy, own_policy=None):
    return CLOUD_DEFAULT | global_policy.get("cloudService", {}) | (own_policy or {}).get("cloudService", {})


class CloudAccount(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)
    id: str = Field(min_length=1, max_length=128)
    email: str = Field(max_length=320)
    displayName: str = Field(max_length=120)

    @field_validator("id", "email", "displayName")
    @classmethod
    def plain_text(cls, value):
        if any(ord(char) < 32 or 127 <= ord(char) <= 159 for char in value):
            raise ValueError("账号身份不能包含控制字符。")
        return value


class CloudStatus(BaseModel):
    model_config = ConfigDict(extra="forbid")
    sequence: int = Field(ge=1, le=9007199254740991, strict=True)
    policyRevision: int = Field(ge=0, le=9007199254740991, strict=True)
    enabled: bool = Field(strict=True)
    authentication: Literal["signedOut", "authorizing", "signedIn", "reauthRequired"]
    connection: Literal["disabled", "connecting", "connected", "offline", "error"]
    account: CloudAccount | None = None
    sessionId: UUID | None = None
    errorCode: Literal["network", "authorization", "credentialStore", "service"] | None = None

    @model_validator(mode="after")
    def consistent(self):
        if (not self.enabled) != (self.connection == "disabled"):
            raise ValueError("连接开关与状态不一致。")
        if self.authentication == "signedIn":
            if self.account is None or self.sessionId is None:
                raise ValueError("登录状态需要账号和会话 ID。")
        elif self.account is not None or self.sessionId is not None:
            raise ValueError("未登录状态不能包含账号或会话 ID。")
        return self


class CloudCommandReceipt(BaseModel):
    model_config = ConfigDict(extra="forbid")
    status: Literal["completed", "skipped", "failed"]
    revocation: Literal["revoked", "pending", "notRequired"]
    errorCode: Literal["sessionMismatch", "credentialStore", "network", "service"] | None = None

    @model_validator(mode="after")
    def consistent(self):
        if self.status == "skipped" and (self.revocation != "notRequired" or self.errorCode != "sessionMismatch"):
            raise ValueError("跳过命令需要 sessionMismatch 和 notRequired。")
        if self.status == "failed" and self.errorCode not in ("credentialStore", "service"):
            raise ValueError("失败命令需要凭据存储或服务错误类别。")
        if self.status == "completed" and (self.errorCode not in (None, "network", "service") or self.revocation == "revoked" and self.errorCode is not None):
            raise ValueError("退出完成回执无效。")
        return self


class CloudManagement:
    def __init__(self, db, store):
        self.db, self.store = db, store
        with db() as conn:
            conn.executescript("""
                CREATE TABLE IF NOT EXISTS cloud_status(device_id TEXT PRIMARY KEY, sequence INTEGER NOT NULL, payload BLOB NOT NULL, reported REAL NOT NULL);
                CREATE TABLE IF NOT EXISTS cloud_commands(id TEXT PRIMARY KEY, device_id TEXT NOT NULL, target_session TEXT NOT NULL, created REAL NOT NULL,
                    status TEXT NOT NULL DEFAULT 'pending', revocation TEXT, error_code TEXT, finished REAL);
                CREATE UNIQUE INDEX IF NOT EXISTS cloud_one_pending ON cloud_commands(device_id) WHERE status='pending';
                CREATE INDEX IF NOT EXISTS cloud_command_device ON cloud_commands(device_id,created);
                CREATE TABLE IF NOT EXISTS cloud_command_requests(id TEXT PRIMARY KEY, device_id TEXT NOT NULL, command_id TEXT NOT NULL);
            """)

    @staticmethod
    def require_capability(request):
        if "cloudServiceV1" not in {part.strip() for part in request.headers.get("x-il-capabilities", "").split(",")}:
            raise HTTPException(409, "客户端需声明 cloudServiceV1。")

    @staticmethod
    def command(row):
        return {"id": row["id"], "type": "logout", "targetSessionId": row["target_session"], "createdAt": row["created"]}

    def pending(self, conn, identifier):
        return [self.command(row) for row in conn.execute("SELECT * FROM cloud_commands WHERE device_id=? AND status='pending' ORDER BY created", (identifier,))]

    def report(self, identifier, payload):
        with self.db() as conn:
            conn.execute("BEGIN IMMEDIATE")
            previous = conn.execute("SELECT sequence FROM cloud_status WHERE device_id=?", (identifier,)).fetchone()
            stale = previous is not None and previous["sequence"] >= payload.sequence
            if not stale:
                conn.execute("INSERT INTO cloud_status VALUES(?,?,?,?) ON CONFLICT(device_id) DO UPDATE SET sequence=excluded.sequence,payload=excluded.payload,reported=excluded.reported",
                             (identifier, payload.sequence, self.store.encode(payload.model_dump(mode="json")), time.time()))
        return {"accepted": True, "stale": stale}

    def enqueue_logout(self, identifier, command_id):
        with self.db() as conn:
            conn.execute("BEGIN IMMEDIATE")
            device = conn.execute("SELECT capabilities,enabled FROM devices WHERE id=?", (identifier,)).fetchone()
            if device is None:
                raise HTTPException(404, "设备不存在。")
            existing = conn.execute("SELECT c.* FROM cloud_command_requests r JOIN cloud_commands c ON c.id=r.command_id WHERE r.id=?", (command_id,)).fetchone()
            if existing:
                if existing["device_id"] != identifier:
                    raise HTTPException(409, "命令 ID 已用于其他设备。")
                return self.command(existing)
            if not device["enabled"] or "cloudServiceV1" not in device["capabilities"].split(","):
                raise HTTPException(409, "设备已禁用或尚未支持云服务管理。")
            existing = conn.execute("SELECT * FROM cloud_commands WHERE device_id=? AND status='pending'", (identifier,)).fetchone()
            if existing:
                conn.execute("INSERT INTO cloud_command_requests VALUES(?,?,?)", (command_id, identifier, existing["id"]))
                return self.command(existing)
            snapshot = conn.execute("SELECT payload FROM cloud_status WHERE device_id=?", (identifier,)).fetchone()
            state = self.store.decode(snapshot["payload"]) if snapshot else None
            if not state or state["authentication"] != "signedIn":
                raise HTTPException(409, "设备尚未报告已登录的云账号。")
            conn.execute("INSERT INTO cloud_commands(id,device_id,target_session,created) VALUES(?,?,?,?)", (command_id, identifier, state["sessionId"], time.time()))
            conn.execute("INSERT INTO cloud_command_requests VALUES(?,?,?)", (command_id, identifier, command_id))
            conn.execute("INSERT INTO audit VALUES(?,?,?)", (time.time(), "cloud_logout", identifier))
            return self.command(conn.execute("SELECT * FROM cloud_commands WHERE id=?", (command_id,)).fetchone())

    def receipt(self, identifier, command_id, payload):
        with self.db() as conn:
            conn.execute("BEGIN IMMEDIATE")
            row = conn.execute("SELECT * FROM cloud_commands WHERE id=? AND device_id=?", (command_id, identifier)).fetchone()
            if row is None:
                raise HTTPException(404, "命令不存在。")
            advance = row["status"] == "completed" and row["revocation"] == "pending" and payload.status == "completed" and payload.revocation == "revoked"
            if row["status"] != "pending" and not advance:
                return {"accepted": True, "stale": True}
            conn.execute("UPDATE cloud_commands SET status=?,revocation=?,error_code=?,finished=? WHERE id=?",
                         (payload.status, payload.revocation, payload.errorCode, time.time(), command_id))
        return {"accepted": True, "stale": False}

    def dashboard(self, identifier, capabilities, policy, online):
        with self.db() as conn:
            snapshot = conn.execute("SELECT payload,reported FROM cloud_status WHERE device_id=?", (identifier,)).fetchone()
            row = conn.execute("SELECT * FROM cloud_commands WHERE device_id=? ORDER BY created DESC LIMIT 1", (identifier,)).fetchone()
        state = self.store.decode(snapshot["payload"]) if snapshot else None
        command = self.command(row) | {"status": row["status"], "revocation": row["revocation"], "errorCode": row["error_code"], "finishedAt": row["finished"]} if row else None
        supported = "cloudServiceV1" in capabilities.split(",")
        status_text = "尚未上报云状态"
        account_text = ""
        if state:
            status_text = {"disabled": "云连接已关闭", "connecting": "云服务连接中", "connected": "云服务已连接", "offline": "云服务离线", "error": "云服务连接异常"}[state["connection"]]
            status_text += " · " + {"signedOut": "未登录", "authorizing": "授权中", "signedIn": "已登录", "reauthRequired": "需要重新授权"}[state["authentication"]]
            if state["account"]:
                account = state["account"]
                account_text = " · ".join(dict.fromkeys(value for value in (account["displayName"], account["email"]) if value)) or account["id"]
        command_text = ""
        if command:
            command_text = {"pending": "远程退出：等待终端执行", "completed": "远程退出：本机账号已清理", "skipped": "远程退出：登录会话已变化，已跳过", "failed": "远程退出失败，请重新下发"}[command["status"]]
            if command["revocation"] == "pending":
                command_text += "，云端撤销待确认"
        return {"supported": supported, "policy": policy, "state": state,
                "reportedAt": snapshot["reported"] if snapshot else None, "stale": not online or snapshot is None, "command": command,
                "statusText": status_text, "accountText": account_text, "commandText": command_text,
                "reportedText": datetime.fromtimestamp(snapshot["reported"]).astimezone().strftime("%m-%d %H:%M:%S") if snapshot else "",
                "warning": "需升级客户端以管理云服务" if not supported else "",
                "canLogout": supported and state is not None and state["authentication"] == "signedIn" and not (command and command["status"] == "pending")}

    @staticmethod
    def clear(conn, identifier):
        conn.execute("DELETE FROM cloud_status WHERE device_id=?", (identifier,))
        conn.execute("DELETE FROM cloud_command_requests WHERE device_id=?", (identifier,))
        conn.execute("DELETE FROM cloud_commands WHERE device_id=?", (identifier,))
