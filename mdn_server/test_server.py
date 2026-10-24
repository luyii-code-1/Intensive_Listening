import base64
import io
import zipfile
import hashlib
import json
from pathlib import Path
import re
import secrets
import tempfile
import unittest
import uuid
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import padding, rsa
from fastapi.testclient import TestClient
from starlette.websockets import WebSocketDisconnect
from server import SecretStore, canonical, create_app, password_hash, initialize, MDN_LOOP, apply_transport


class ManagementTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.private = rsa.generate_private_key(public_exponent=65537, key_size=2048)
        salt = secrets.token_hex(16)
        self.config = {"organization_name": "测试学校", "enrollment_token": secrets.token_urlsafe(48), "session_secret": secrets.token_urlsafe(48), "password_salt": salt, "password_hash": password_hash("test-password-123", salt), "public_url": "https://mdn.example.edu/", "signing_key": self.private.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8, serialization.NoEncryption()).decode()}
        self.client = TestClient(create_app(self.root, self.config), base_url="https://mdn.example.edu")
        self.identifier = str(uuid.uuid4())
        self.token = self.register(self.identifier)
        self.headers = {"Authorization": "Bearer " + self.token, "X-IL-Capabilities": "transcriptBlurIntensity,lessonDistribution"}

    def tearDown(self):
        self.client.close(); self.temp.cleanup()

    def test_receipts_reject_non_object_and_invalid_json(self):
        for path in ("/api/policy/receipt", '/api/connection', '/api/updates/unknown/receipt'):
            for body in ("[]", "null", "{", "42"):
                with self.subTest(path=path, body=body):
                    response = self.client.post(path, headers={**self.headers, "Content-Type": "application/json"}, content=body)
                    self.assertEqual(response.status_code, 422)

    def test_connection_rejects_boolean_latency(self):
        self.assertEqual(self.client.post('/api/connection', headers=self.headers, json={"latency": True}).status_code, 422)

    def test_binary_realtime_frame_is_rejected_and_reconnect_works(self):
        with self.client.websocket_connect("/api/connect", headers=self.headers) as socket:
            self.assertEqual(socket.receive_json()["type"], "ready")
            socket.send_bytes(b"invalid frame")
            with self.assertRaises(WebSocketDisconnect) as closed:
                socket.receive_json()
            self.assertEqual(closed.exception.code, 4400)
        with self.client.websocket_connect("/api/connect", headers=self.headers) as socket:
            self.assertEqual(socket.receive_json()["type"], "ready")
            socket.send_json({"type": "heartbeat", "sentAt": 1})
            self.assertEqual(socket.receive_json()["type"], "heartbeat")

    def register(self, identifier):
        response = self.client.post("/api/register", json={"deviceId": identifier, "deviceName": "IL-LAB", "appVersion": "2.0.1", "enrollmentToken": self.config["enrollment_token"]})
        self.assertEqual(response.status_code, 200)
        return response.json()["deviceToken"]

    def login(self):
        html = self.client.get("/").text
        csrf = re.search(r'name="form_token" value="([^"]+)"', html)[1]
        self.assertEqual(self.client.post("/login", data={"password": "test-password-123", "form_token": csrf}).status_code, 200)
        return re.search(r'name="form_token" value="([^"]+)"', self.client.get("/admin").text)[1]

    def save(self, scope, policy, csrf):
        return self.client.post("/admin/policy", data={"scope": scope, "policy_json": json.dumps(policy), "form_token": csrf})

    def cloud_report(self, sequence=1, **changes):
        headers = self.headers | {"X-IL-Capabilities": "clientRoleV1,asrModeV1,cloudServiceV1"}
        policy = self.client.get("/api/policy", headers=headers).json()
        payload = {"sequence": sequence, "policyRevision": policy["revision"], "enabled": True,
                   "authentication": "signedIn", "connection": "connected",
                   "account": {"id": "user-123", "email": "learner@example.com", "displayName": "学习者"},
                   "sessionId": str(uuid.uuid4()), "errorCode": None} | changes
        response = self.client.post("/api/cloud/status", headers=headers, json=payload)
        self.assertEqual(response.status_code, 200, response.text)
        return headers, payload

    def cloud_logout(self, csrf, command_id=None, identifier=None):
        return self.client.post("/admin/cloud/logout", data={"device_id": identifier or self.identifier,
            "command_id": command_id or str(uuid.uuid4()), "form_token": csrf})

    def test_cloud_policy_nested_inheritance_and_legacy_filter(self):
        csrf = self.login()
        self.assertEqual(self.save("all", {"clientRole": "student", "asrMode": "forceCloud", "cloudService": {"mode": "enabled", "requireLogin": True, "lockAccount": True}}, csrf).status_code, 200)
        self.assertEqual(self.save(self.identifier, {"cloudService": {"requireLogin": False}}, csrf).status_code, 200)
        headers, _ = self.cloud_report()
        policy = self.client.get("/api/policy", headers=headers).json()
        self.assertEqual(policy["cloudService"], {"mode": "enabled", "requireLogin": False, "lockAccount": True})
        self.assertEqual(policy["clientRole"], "student")
        self.assertEqual(policy["asrMode"], "forceCloud")
        legacy = self.client.get("/api/policy", headers=self.headers).json()
        self.assertNotIn("cloudService", legacy)
        self.assertNotIn("cloudCommands", legacy)
        self.client.post("/admin/device", data={"device_id": self.identifier, "action": "inherit", "form_token": csrf})
        self.assertTrue(self.client.get("/api/policy", headers=headers).json()["cloudService"]["requireLogin"])
        self.save("all", {}, csrf)
        self.assertEqual(self.client.get("/api/policy", headers=headers).json()["cloudService"], {"mode": "followLocal", "requireLogin": False, "lockAccount": False})
        for value in (None, [], {"mode": "on"}, {"lockAccount": 1}, {"requireLogin": "true"}, {"token": "secret"}):
            self.assertEqual(self.save("all", {"cloudService": value}, csrf).status_code, 422)

    def test_cloud_status_sequence_encryption_and_field_validation(self):
        import sqlite3
        from contextlib import closing
        csrf = self.login()
        headers, payload = self.cloud_report(sequence=3)
        old = payload | {"sequence": 2, "account": {"id": "older", "email": "", "displayName": ""}}
        self.assertTrue(self.client.post("/api/cloud/status", headers=headers, json=old).json()["stale"])
        self.assertTrue(self.client.post("/api/cloud/status", headers=headers, json=payload).json()["stale"])
        cloud = self.client.get("/admin/status").json()["devices"][0]["cloud"]
        self.assertEqual(cloud["state"]["account"]["id"], "user-123")
        self.assertTrue(cloud["supported"])
        with closing(sqlite3.connect(self.root / "mdn.sqlite3")) as conn:
            blob = conn.execute("SELECT payload FROM cloud_status").fetchone()[0]
        self.assertNotIn(b"learner@example.com", blob)
        self.assertEqual(SecretStore().decode(blob), payload)
        self.assertEqual(self.client.post("/api/cloud/status", json=payload).status_code, 401)
        self.assertEqual(self.client.post("/api/cloud/status", headers=self.headers, json=payload).status_code, 409)
        for changed in ({"sequence": True}, {"policyRevision": payload["policyRevision"] + 1}, {"accessToken": "secret"},
                        {"account": payload["account"] | {"displayName": "bad\nname"}}, {"authentication": "signedOut"},
                        {"enabled": False}, {"errorCode": "token=secret"}, {"sessionId": "bad"}):
            self.assertEqual(self.client.post("/api/cloud/status", headers=headers, json=payload | changed).status_code, 422)
        self.cloud_report(sequence=4, authentication="signedOut", account=None, sessionId=None)
        self.assertIsNone(self.client.get("/admin/status").json()["devices"][0]["cloud"]["state"]["account"])

    def test_cloud_logout_idempotency_session_binding_and_receipt_isolation(self):
        csrf = self.login()
        headers, payload = self.cloud_report()
        request_id, alias_id = str(uuid.uuid4()), str(uuid.uuid4())
        first = self.cloud_logout(csrf, request_id).json()["command"]
        self.assertEqual(first["targetSessionId"], payload["sessionId"])
        self.assertEqual(self.cloud_logout(csrf, request_id).json()["command"], first)
        self.assertEqual(self.cloud_logout(csrf, alias_id).json()["command"], first)
        policy = self.client.get("/api/policy", headers=headers).json()
        self.assertEqual(policy["revision"], payload["policyRevision"])
        self.assertEqual(policy["cloudCommands"], [first])
        other_id = str(uuid.uuid4())
        other = self.register(other_id)
        self.assertEqual(self.cloud_logout(csrf, request_id, other_id).status_code, 409)
        receipt = "/api/cloud/commands/" + first["id"] + "/receipt"
        skipped = {"status": "skipped", "revocation": "notRequired", "errorCode": "sessionMismatch"}
        self.assertEqual(self.client.post(receipt, headers={"Authorization": "Bearer " + other, "X-IL-Capabilities": "cloudServiceV1"}, json=skipped).status_code, 404)
        self.cloud_report(sequence=2)
        self.assertFalse(self.client.post(receipt, headers=headers, json=skipped).json()["stale"])
        self.assertEqual(self.client.get("/api/policy", headers=headers).json()["cloudCommands"], [])
        self.assertEqual(self.cloud_logout(csrf, alias_id).json()["command"], first)
        self.assertEqual(self.client.get("/api/policy", headers=headers).json()["cloudCommands"], [])
        fresh = self.cloud_logout(csrf).json()["command"]
        self.assertNotEqual(fresh["targetSessionId"], first["targetSessionId"])

    def test_cloud_logout_receipt_terminal_monotonicity(self):
        csrf = self.login()
        headers, _ = self.cloud_report()
        command = self.cloud_logout(csrf).json()["command"]
        receipt = "/api/cloud/commands/" + command["id"] + "/receipt"
        body = {"status": "completed", "revocation": "pending", "errorCode": "network"}
        self.assertFalse(self.client.post(receipt, headers=headers, json=body).json()["stale"])
        regressed = {"status": "failed", "revocation": "notRequired", "errorCode": "credentialStore"}
        self.assertTrue(self.client.post(receipt, headers=headers, json=regressed).json()["stale"])
        self.assertIn("云端撤销待确认", self.client.get("/admin/status").json()["devices"][0]["cloud"]["commandText"])
        final = {"status": "completed", "revocation": "revoked", "errorCode": None}
        self.assertFalse(self.client.post(receipt, headers=headers, json=final).json()["stale"])
        self.assertTrue(self.client.post(receipt, headers=headers, json=body).json()["stale"])
        self.assertEqual(self.client.get("/admin/status").json()["devices"][0]["cloud"]["command"]["revocation"], "revoked")
        for invalid in ({"status": "skipped", "revocation": "revoked"}, final | {"errorCode": "raw exception"}, final | {"token": "secret"}):
            self.assertEqual(self.client.post(receipt, headers=headers, json=invalid).status_code, 422)

    def test_cloud_logout_push_persistence_and_registration_cleanup(self):
        import sqlite3
        import time
        from contextlib import closing
        csrf = self.login()
        headers, _ = self.cloud_report()
        with self.client.websocket_connect("/api/connect", headers=headers) as socket:
            socket.receive_json()
            command = self.cloud_logout(csrf).json()["command"]
            self.assertEqual(socket.receive_json()["type"], "policy")
            socket.send_json({"type": "heartbeat", "sentAt": 42})
            self.assertEqual(socket.receive_json()["cloudCommandId"], command["id"])
        self.client.close()
        self.client = TestClient(create_app(self.root, self.config), base_url="https://mdn.example.edu")
        self.assertEqual(self.client.get("/api/policy", headers=headers).json()["cloudCommands"], [command])
        with closing(sqlite3.connect(self.root / "mdn.sqlite3")) as conn:
            conn.execute("UPDATE devices SET last_seen=? WHERE id=?", (time.time() - 120, self.identifier))
            conn.commit()
        csrf = self.login()
        cloud = self.client.get("/admin/status").json()["devices"][0]["cloud"]
        self.assertEqual(cloud["state"]["account"]["email"], "learner@example.com")
        self.assertTrue(cloud["stale"])
        self.assertIn('data-cloud-option="mode"', self.client.get("/admin").text)
        self.assertIn("远程退出云账号", self.client.get("/admin").text)
        self.client.post("/admin/device", data={"device_id": self.identifier, "action": "reset", "form_token": csrf})
        with closing(sqlite3.connect(self.root / "mdn.sqlite3")) as conn:
            for table in ("cloud_status", "cloud_commands", "cloud_command_requests"):
                self.assertEqual(conn.execute("SELECT COUNT(*) FROM " + table).fetchone()[0], 0)

    def test_cloud_logout_requires_known_account_capability_and_csrf(self):
        csrf = self.login()
        self.assertEqual(self.cloud_logout(csrf).status_code, 409)
        headers, payload = self.cloud_report()
        self.assertEqual(self.cloud_logout("invalid").status_code, 403)
        self.cloud_report(sequence=2, authentication="signedOut", account=None, sessionId=None)
        self.assertEqual(self.cloud_logout(csrf).status_code, 409)
        self.cloud_report(sequence=3, enabled=False, connection="disabled")
        self.assertEqual(self.cloud_logout(csrf).status_code, 200)
        self.client.post("/admin/device", data={"device_id": self.identifier, "action": "allow_leave", "form_token": csrf})
        self.assertEqual(self.client.post("/api/leave", headers=headers).status_code, 200)
        self.token = self.register(self.identifier)
        self.headers = {"Authorization": "Bearer " + self.token, "X-IL-Capabilities": "cloudServiceV1"}
        self.assertEqual(self.client.get("/api/policy", headers=self.headers).json()["cloudCommands"], [])
        self.cloud_report()

    def test_student_role_legacy_fallback_and_modern_capability(self):
        csrf = self.login()
        self.assertEqual(self.save("all", {"clientRole": "student", "disabledFeatures": ["updates"]}, csrf).status_code, 200)
        legacy = self.client.get("/api/policy", headers=self.headers).json()
        self.assertNotIn("clientRole", legacy)
        self.assertEqual(set(legacy["disabledFeatures"]), {"updates", "authoring", "asr", "mcp", "export", "importProject", "apiConfiguration"})
        self.assertTrue(self.client.get("/admin/status").json()["devices"][0]["role_warning"])
        headers = self.headers | {"X-IL-Capabilities": "transcriptBlurIntensity, clientRoleV1"}
        modern = self.client.get("/api/policy", headers=headers).json()
        self.assertEqual(modern["clientRole"], "student")
        device = self.client.get("/admin/status").json()["devices"][0]
        self.assertEqual(device["role_label"], "学生端")
        self.assertTrue(device["role_supported"])
        self.assertEqual(device["role_warning"], "")
        self.assertIn('id="client-role"', self.client.get("/admin?view=policy").text)
        self.assertIn('class="role-state">学生端', self.client.get("/admin").text)

    def test_student_role_inheritance_explicit_default_and_revoke(self):
        csrf = self.login()
        headers = self.headers | {"X-IL-Capabilities": "clientRoleV1"}
        self.save("all", {"clientRole": "student", "settings": {"cloudConcurrency": 2}, "disabledFeatures": ["updates"]}, csrf)
        self.save(self.identifier, {"disabledFeatures": []}, csrf)
        inherited = self.client.get("/api/policy", headers=headers).json()
        self.assertEqual(inherited["clientRole"], "student")
        self.assertIn("authoring", inherited["disabledFeatures"])
        self.save(self.identifier, {"clientRole": "default"}, csrf)
        ordinary = self.client.get("/api/policy", headers=headers).json()
        self.assertEqual(ordinary["clientRole"], "default")
        self.assertEqual(ordinary["disabledFeatures"], ["updates"])
        self.assertEqual(ordinary["settings"]["cloudConcurrency"], 2)
        self.client.post("/admin/device", data={"device_id": self.identifier, "action": "inherit", "form_token": csrf})
        self.assertEqual(self.client.get("/api/policy", headers=headers).json()["clientRole"], "student")
        self.save("all", {"disabledFeatures": ["updates"], "lockedSettings": ["cloudConcurrency"]}, csrf)
        revoked = self.client.get("/api/policy", headers=headers).json()
        self.assertEqual(revoked["clientRole"], "default")
        self.assertEqual(revoked["disabledFeatures"], ["updates"])
        self.assertEqual(revoked["lockedSettings"], ["cloudConcurrency"])

    def test_role_change_notifies_and_waits_for_current_receipt(self):
        csrf = self.login()
        previous = self.client.get("/api/policy", headers=self.headers).json()["revision"]
        self.client.post("/api/policy/receipt", headers=self.headers, json={"revision": previous})
        with self.client.websocket_connect("/api/connect", headers=self.headers) as socket:
            socket.receive_json()
            self.save(self.identifier, {"clientRole": "student"}, csrf)
            self.assertEqual(socket.receive_json()["type"], "policy")
            device = self.client.get("/admin/status").json()["devices"][0]
            self.assertFalse(device["policy_current"])
            self.assertEqual(device["client_role"], "student")
            current = self.client.get("/api/policy", headers=self.headers).json()["revision"]
            self.client.post("/api/policy/receipt", headers=self.headers, json={"revision": current})
            self.assertTrue(self.client.get("/admin/status").json()["devices"][0]["policy_current"])

    def test_role_persists_and_capability_column_migrates(self):
        import sqlite3
        from contextlib import closing
        csrf = self.login()
        self.save(self.identifier, {"clientRole": "student", "asrMode": "forceCloud"}, csrf)
        with closing(sqlite3.connect(self.root / "mdn.sqlite3")) as conn:
            conn.execute("ALTER TABLE devices DROP COLUMN capabilities")
            conn.commit()
        self.client.close()
        self.client = TestClient(create_app(self.root, self.config), base_url="https://mdn.example.edu")
        headers = self.headers | {"X-IL-Capabilities": "clientRoleV1,asrModeV1"}
        restored = self.client.get("/api/policy", headers=headers).json()
        self.assertEqual(restored["clientRole"], "student")
        self.assertEqual(restored["asrMode"], "forceCloud")
        self.login()
        self.assertTrue(self.client.get("/admin/status").json()["devices"][0]["role_supported"])
        self.client.get("/api/policy", headers=self.headers)
        self.assertFalse(self.client.get("/admin/status").json()["devices"][0]["role_supported"])

    def test_invalid_role_and_asr_mode_are_rejected(self):
        csrf = self.login()
        for value in ("teacher", None, 1, [], {}):
            self.assertEqual(self.save("all", {"clientRole": value}, csrf).status_code, 422)
        for value in ("cloud", "local", None, 1, [], {}):
            self.assertEqual(self.save("all", {"asrMode": value}, csrf).status_code, 422)

    def test_asr_mode_inheritance_capability_and_unlock(self):
        csrf = self.login()
        headers = self.headers | {"X-IL-Capabilities": "clientRoleV1,asrModeV1"}
        self.save("all", {"asrMode": "forceApi", "settings": {"cloudModel": "model"}}, csrf)
        self.save(self.identifier, {"clientRole": "default"}, csrf)
        inherited = self.client.get("/api/policy", headers=headers).json()
        self.assertEqual(inherited["asrMode"], "forceApi")
        self.assertEqual(inherited["settings"]["cloudModel"], "model")
        self.assertTrue(self.client.get("/admin/status").json()["devices"][0]["asr_supported"])
        self.assertNotIn("asrMode", self.client.get("/api/policy", headers=self.headers).json())
        device = self.client.get("/admin/status").json()["devices"][0]
        self.assertEqual(device["asr_label"], "强制传统 API")
        self.assertTrue(device["asr_warning"])
        self.save(self.identifier, {"asrMode": "forceCloud"}, csrf)
        self.assertEqual(self.client.get("/api/policy", headers=headers).json()["asrMode"], "forceCloud")
        self.assertFalse(self.client.get("/admin/status").json()["devices"][0]["asr_warning"])
        self.save(self.identifier, {"asrMode": "followLocal"}, csrf)
        self.assertEqual(self.client.get("/api/policy", headers=headers).json()["asrMode"], "followLocal")
        self.client.post("/admin/device", data={"device_id": self.identifier, "action": "inherit", "form_token": csrf})
        self.assertEqual(self.client.get("/api/policy", headers=headers).json()["asrMode"], "forceApi")
        self.assertIn('id="asr-mode"', self.client.get("/admin?view=policy").text)

    def test_policy_receipt_is_monotonic_and_dashboard_shows_pending(self):
        csrf = self.login()
        revision = self.client.get("/api/policy", headers=self.headers).json()["revision"]
        response = self.client.post("/api/policy/receipt", headers=self.headers, json={"revision": revision})
        self.assertEqual(response.status_code, 200)
        device = self.client.get("/admin/status").json()["devices"][0]
        self.assertTrue(device["policy_current"])
        self.assertEqual(device["policy_state"], "策略已接收")
        self.assertEqual(self.client.post("/api/policy/receipt", headers=self.headers, json={"revision": revision + 1}).status_code, 422)
        self.save("all", {"settings": {"cloudConcurrency": 2}}, csrf)
        self.assertFalse(self.client.get("/admin/status").json()["devices"][0]["policy_current"])
        latest = self.client.get("/api/policy", headers=self.headers).json()["revision"]
        self.client.post("/api/policy/receipt", headers=self.headers, json={"revision": latest})
        self.client.post("/api/policy/receipt", headers=self.headers, json={"revision": revision})
        self.assertTrue(self.client.get("/admin/status").json()["devices"][0]["policy_current"])

    def test_credentials_and_device_revocation(self):
        self.assertEqual(self.client.get("/api/policy").status_code, 401)
        payload = {"deviceId": self.identifier, "deviceName": "impersonator", "appVersion": "2.0.1", "enrollmentToken": self.config["enrollment_token"]}
        self.assertEqual(self.client.post("/api/register", json=payload).status_code, 409)
        csrf = self.login()
        self.client.post("/admin/device", data={"device_id": self.identifier, "action": "disable", "form_token": csrf})
        self.assertEqual(self.client.get("/api/policy", headers=self.headers).status_code, 403)

    def test_full_error_track_roundtrip(self):
        details = "System.InvalidOperationException: outer\nInnerException: nested\n" + "   at Example.Method() in /_/src/Example.cs:line 10\n" * 450
        metrics = {"reportId": "complete-report", "exceptionType": "System.InvalidOperationException", "component": "主程序", "fatal": True,
                   "message": "outer", "details": details, "recentOperations": "open queue\nclose queue\nopen queue", "platform": "Windows", "architecture": "X64", "processId": 123, "exitCode": 1}
        response = self.client.post("/api/telemetry", headers=self.headers, json={"id": "complete-report", "kind": "error_track", "occurredAt": "2026-10-15T12:09:07+08:00", "appVersion": "2.1.0.1", "metrics": metrics})
        self.assertEqual(response.status_code, 200)
        import sqlite3
        with sqlite3.connect(self.root / "mdn.sqlite3") as connection:
            stored = json.loads(connection.execute("SELECT metrics FROM events WHERE event_id='complete-report'").fetchone()[0])
        connection.close()
        self.assertEqual(stored, metrics)
        self.login()
        page = self.client.get("/admin?view=telemetry&kind=error_track").text
        self.assertIn("完整异常", page)
        self.assertIn("最近操作", page)
        self.assertIn("nested", page)

    def test_alias_diagnostic_search_and_connection_display(self):
        csrf = self.login()
        self.assertEqual(self.client.post("/admin/device/alias", data={"device_id": self.identifier, "alias": "教室一", "form_token": csrf}).status_code, 200)
        self.client.post("/api/telemetry", headers=self.headers, json={"id": "searchable", "kind": "asr", "occurredAt": "2026-10-14T00:00:00+08:00", "appVersion": "2.1.0.1", "metrics": {"success": False, "errorType": "InvalidTranscript", "durationMs": 100}})
        html = self.client.get("/admin?view=telemetry&q=教室一&kind=asr").text
        self.assertIn("InvalidTranscript", html); self.assertIn("识别成功", html)
        self.assertNotIn("InvalidTranscript", self.client.get("/admin?view=telemetry&q=unmatched").text)
        self.client.get("/api/policy", headers=self.headers)
        self.assertIn("轮询模式", self.client.get("/admin/status").json()["devices"][0]["connection"])
        with self.client.websocket_connect("/api/connect", headers=self.headers) as socket:
            socket.receive_json()
            self.client.post('/api/connection', headers=self.headers, json={"latency": 42})
            self.assertEqual(self.client.get("/admin/status").json()["devices"][0]["connection"], "实时模式 - 42ms")

    def test_update_receipts_retry_and_history_cleanup(self):
        csrf = self.login()
        fields = {"form_token": csrf, "scope": self.identifier, "mode": "ota", "version": "2.1.0.1", "url": "https://mdn.example.edu/setup.exe", "sha256": "a"*64, "size": 1024}
        self.assertEqual(self.client.post("/admin/update", data=fields).status_code, 200)
        task = self.client.get("/api/policy", headers=self.headers).json()["update"]
        receipt = "/api/updates/" + task["id"] + "/receipt"
        self.assertEqual(self.client.post(receipt, headers=self.headers, json={"status": "downloading", "progress": 35}).status_code, 200)
        self.assertIn("35%", self.client.get("/admin?view=updates").text)
        other = self.register(str(uuid.uuid4()))
        self.assertEqual(self.client.post(receipt, headers={"Authorization":"Bearer "+other}, json={"status":"success"}).status_code, 404)
        self.client.post(receipt, headers=self.headers, json={"status":"failed", "message":"UAC cancelled"})
        self.assertEqual(self.client.post("/admin/updates/retry", data={"form_token":csrf,"task_id":task["id"],"device_id":self.identifier}).status_code, 200)
        retry = self.client.get("/api/policy", headers=self.headers).json()["update"]
        self.assertNotEqual(task["id"], retry["id"])
        self.assertEqual(self.client.post("/admin/updates/clear", data={"form_token":csrf}).status_code, 200)
        self.assertNotIn(task["id"], self.client.get("/admin?view=updates").text)
        self.assertIn(retry["id"], self.client.get("/admin?view=updates").text)

    def publish_task(self, csrf, scope, version="2.1.2"):
        fields = {"form_token": csrf, "scope": scope, "mode": "ota", "version": version, "url": "https://mdn.example.edu/setup.exe", "sha256": "a"*64, "size": 1024}
        self.assertEqual(self.client.post("/admin/update", data=fields).status_code, 200)
        return self.client.get("/api/policy", headers=self.headers).json()["update"]

    def test_latest_assignment_replaces_single_and_global_without_changing_settings(self):
        csrf = self.login()
        second = self.register(str(uuid.uuid4()))
        other_headers = {"Authorization": "Bearer " + second}
        self.save(self.identifier, {"settings": {"themeMode": "dark"}}, csrf)
        first = self.publish_task(csrf, "all", "2.1.2")
        single = self.publish_task(csrf, self.identifier, "2.1.3")
        self.assertNotEqual(first["id"], single["id"])
        latest = self.publish_task(csrf, "all", "2.1.4")
        policy = self.client.get("/api/policy", headers=self.headers).json()
        self.assertEqual(policy["update"]["id"], latest["id"])
        self.assertEqual(policy["settings"]["themeMode"], "dark")
        self.assertEqual(self.client.get("/api/policy", headers=other_headers).json()["update"]["id"], latest["id"])
        html = self.client.get("/admin/status?updates=true").json()["updates_html"]
        self.assertIn('data-replacement-count="2"', html)
        self.assertIn("已被新任务替换", html)
        self.client.post("/admin/update/cancel", data={"form_token": csrf, "scope": self.identifier})
        self.assertIsNone(self.client.get("/api/policy", headers=self.headers).json()["update"])
        self.assertEqual(self.client.get("/api/policy", headers=other_headers).json()["update"]["id"], latest["id"])
        newest = self.publish_task(csrf, "all", "2.1.5")
        self.assertEqual(self.client.get("/api/policy", headers=self.headers).json()["update"]["id"], newest["id"])

    def test_late_receipts_cannot_regress_ready_or_success(self):
        csrf = self.login(); task = self.publish_task(csrf, "all")
        receipt = "/api/updates/" + task["id"] + "/receipt"
        for body in [{"status": "ready", "progress": 100}, {"status": "downloading", "progress": 20}]:
            self.assertEqual(self.client.post(receipt, headers=self.headers, json=body).status_code, 200)
        html = self.client.get("/admin/status?updates=true").json()["updates_html"]
        self.assertIn("等待安装", html); self.assertNotIn('class="download-progress"', html)
        self.client.post(receipt, headers=self.headers, json={"status": "success", "progress": 100})
        response = self.client.post(receipt, headers=self.headers, json={"status": "failed", "message": "old failure"})
        self.assertTrue(response.json()["stale"])
        html = self.client.get("/admin/status?updates=true").json()["updates_html"]
        self.assertIn("更新成功", html); self.assertNotIn("old failure", html)

    def test_targets_are_snapshotted_and_cleared_history_stays_cleared_after_restart(self):
        import sqlite3
        csrf = self.login(); first = self.publish_task(csrf, "all"); latest = self.publish_task(csrf, "all", "2.1.3")
        extra_id = str(uuid.uuid4()); self.register(extra_id)
        connection = sqlite3.connect(self.root / "mdn.sqlite3")
        try:
            self.assertEqual(connection.execute("SELECT count(*) FROM update_targets WHERE task_id=?", (first["id"],)).fetchone()[0], 1)
            self.assertEqual(connection.execute("SELECT count(*) FROM update_targets WHERE task_id=?", (latest["id"],)).fetchone()[0], 2)
        finally:
            connection.close()
        self.client.post("/admin/updates/clear", data={"form_token": csrf})
        self.client.close(); self.client = TestClient(create_app(self.root, self.config), base_url="https://mdn.example.edu")
        self.login()
        html = self.client.get("/admin?view=updates").text
        self.assertNotIn(first["id"], html); self.assertIn(latest["id"], html)

    def test_existing_policy_assignments_migrate_and_new_global_supersedes_them(self):
        import sqlite3
        csrf = self.login(); self.publish_task(csrf, "all"); single = self.publish_task(csrf, self.identifier, "2.1.3")
        connection = sqlite3.connect(self.root / "mdn.sqlite3")
        try:
            connection.executescript("DROP TABLE update_assignments; DROP TABLE update_targets;")
        finally:
            connection.close()
        self.client.close(); self.client = TestClient(create_app(self.root, self.config), base_url="https://mdn.example.edu")
        self.assertEqual(self.client.get("/api/policy", headers=self.headers).json()["update"]["id"], single["id"])
        csrf = self.login(); latest = self.publish_task(csrf, "all", "2.1.4")
        self.assertEqual(self.client.get("/api/policy", headers=self.headers).json()["update"]["id"], latest["id"])

    def test_heartbeat_advertises_current_revision_for_missed_push_recovery(self):
        with self.client.websocket_connect("/api/connect", headers=self.headers) as socket:
            socket.receive_json(); socket.send_json({"type": "heartbeat", "sentAt": 100})
            response = socket.receive_json()
            self.assertEqual(response["sentAt"], 100)
            self.assertEqual(response["revision"], self.client.get("/api/policy", headers=self.headers).json()["revision"])

    def test_live_status_includes_new_devices_and_escaped_feedback(self):
        csrf = self.login(); task = self.publish_task(csrf, "all")
        self.client.post("/api/updates/" + task["id"] + "/receipt", headers=self.headers, json={"status": "failed", "message": "<script>failure-marker</script>"})
        extra_id = str(uuid.uuid4()); self.register(extra_id)
        status = self.client.get("/admin/status?updates=true").json()
        self.assertEqual(len(status["devices"]), 2)
        self.assertIn(extra_id, status["device_rows_html"])
        self.assertIn("&lt;script&gt;failure-marker", status["updates_html"])
        self.assertNotIn("<script>failure-marker", status["updates_html"])

    def test_migration_selects_newer_global_instead_of_old_single_task(self):
        import sqlite3
        csrf = self.login(); self.publish_task(csrf, self.identifier); latest = self.publish_task(csrf, "all", "2.1.3")
        connection = sqlite3.connect(self.root / "mdn.sqlite3")
        try:
            connection.executescript("DROP TABLE update_assignments; DROP TABLE update_targets;")
        finally:
            connection.close()
        self.client.close(); self.client = TestClient(create_app(self.root, self.config), base_url="https://mdn.example.edu")
        self.assertEqual(self.client.get("/api/policy", headers=self.headers).json()["update"]["id"], latest["id"])

    def test_expired_admin_session_returns_login_and_preserves_api_auth(self):
        response = self.client.get("/admin", follow_redirects=False)
        self.assertEqual(response.status_code, 303)
        self.assertEqual(response.headers["location"], "/?message=login")
        csrf = self.login()
        self.client.cookies.clear()
        response = self.save("all", {"lockAllSettings": True}, csrf)
        self.assertIn("登录状态失效", response.text)
        self.assertNotIn('id="policy-form"', response.text)
        self.assertFalse(self.client.get("/api/policy", headers=self.headers).json()["lockAllSettings"])
        self.assertEqual(self.client.get("/api/policy").status_code, 401)

    def test_realtime_heartbeat_scoped_push_online_disconnect_and_revocation(self):
        with self.client:
            csrf = self.login()
            second = str(uuid.uuid4())
            token = self.register(second)
            with self.client.websocket_connect("/api/connect", headers=self.headers) as ws:
                self.assertEqual(ws.receive_json()["type"], "ready")
                ws.send_json({"type": "heartbeat"})
                self.assertEqual(ws.receive_json()["type"], "heartbeat")
                self.assertTrue(next(d for d in self.client.get("/admin/status").json()["devices"] if d["id"] == self.identifier)["online"])
                with self.client.websocket_connect("/api/connect", headers={"Authorization": "Bearer " + token}) as other:
                    self.assertEqual(other.receive_json()["type"], "ready")
                    self.save(second, {"settings": {"themeMode": "dark"}}, csrf)
                    self.assertEqual(other.receive_json()["type"], "policy")
                    ws.send_json({"type": "heartbeat"})
                    self.assertEqual(ws.receive_json()["type"], "heartbeat")
                    self.save("all", {"settings": {"fileAssociationEnabled": True}}, csrf)
                    self.assertEqual(ws.receive_json()["type"], "policy")
                    self.assertEqual(other.receive_json()["type"], "policy")
                self.client.post("/admin/device", data={"device_id": self.identifier, "action": "disable", "form_token": csrf})
                with self.assertRaises(WebSocketDisconnect):
                    ws.receive_json()
            self.assertFalse(next(d for d in self.client.get("/admin/status").json()["devices"] if d["id"] == self.identifier)["online"])
            with self.assertRaises(WebSocketDisconnect):
                with self.client.websocket_connect("/api/connect", headers=self.headers):
                    pass
            with self.assertRaises(WebSocketDisconnect):
                with self.client.websocket_connect("/api/connect"):
                    pass

    def test_uploaded_package_signature_scope_download_and_cancellation(self):
        csrf = self.login()
        content = b"MZ" + b"installer-test" * 20000
        query = {"scope": self.identifier, "mode": "cold", "version": "2.0.2", "filename": "setup.exe"}
        response = self.client.post("/admin/update/upload", params=query, content=content, headers={"X-CSRF-Token": csrf})
        self.assertEqual(response.status_code, 200)
        update = self.client.get("/api/policy", headers=self.headers).json()["update"]
        self.assertEqual(update["size"], len(content))
        self.assertEqual(update["sha256"], hashlib.sha256(content).hexdigest())
        self.private.public_key().verify(base64.b64decode(update["signature"]), canonical(update), padding.PKCS1v15(), hashes.SHA256())
        self.assertEqual(self.client.get(update["url"], headers=self.headers).content, content)
        self.assertEqual(self.client.get(update["url"]).status_code, 401)
        other_token = self.register(str(uuid.uuid4()))
        self.assertEqual(self.client.get(update["url"], headers={"Authorization": "Bearer " + other_token}).status_code, 403)
        self.client.post("/admin/update/cancel", data={"scope": self.identifier, "form_token": csrf})
        self.assertEqual(self.client.get(update["url"], headers=self.headers).status_code, 403)
        self.assertFalse(list((self.root / "packages").glob("*.part")))

    def test_upload_rejects_bad_package_and_does_not_change_policy(self):
        csrf = self.login()
        query = {"scope": "all", "mode": "ota", "version": "2.0.2", "filename": "bad.exe"}
        self.assertEqual(self.client.post("/admin/update/upload", params=query, content=b"bad", headers={"X-CSRF-Token": csrf}).status_code, 422)
        self.assertEqual(self.client.post("/admin/update/upload", params=query, content=b"MZ", headers={"X-CSRF-Token": "wrong"}).status_code, 403)
        self.assertEqual(self.client.post("/admin/update/upload", params=query, content=b"MZ", headers={"X-CSRF-Token": csrf, "Content-Length": "2147483649"}).status_code, 413)
        self.assertEqual(list((self.root / "packages").iterdir()), [])
        self.assertIsNone(self.client.get("/api/policy", headers=self.headers).json()["update"])

    def test_manual_discovery_and_admin_authorized_leave(self):
        info = self.client.get("/api/info").json()
        self.assertEqual(info["organizationName"], "测试学校")
        self.assertNotIn("enrollmentToken", info)
        serialization.load_der_public_key(base64.b64decode(info["updatePublicKey"]))
        self.assertEqual(self.client.post("/api/leave", headers=self.headers).status_code, 403)
        csrf = self.login()
        form = {"device_id": self.identifier, "action": "allow_leave", "form_token": csrf}
        self.assertEqual(self.client.post("/admin/device", data=form).status_code, 200)
        self.assertIn("已授权退出", self.client.get("/admin").text)
        form["action"] = "cancel_leave"
        self.client.post("/admin/device", data=form)
        self.assertEqual(self.client.post("/api/leave", headers=self.headers).status_code, 403)
        form["action"] = "allow_leave"
        self.client.post("/admin/device", data=form)
        self.assertEqual(self.client.post("/api/leave", headers=self.headers).status_code, 200)
        self.assertEqual(self.client.get("/api/policy", headers=self.headers).status_code, 403)
        self.register(self.identifier)

    def test_admin_assets_and_expanded_page_upgrade(self):
        csrf = self.login()
        self.assertIn('id="policy-form"', self.client.get("/admin").text)
        self.assertEqual(self.client.get("/assets/admin.js").status_code, 200)
        self.assertEqual(self.client.get("/assets/admin.css").status_code, 200)
        marker = self.root / "web" / "bundle-version.txt"
        marker.write_text("previous-version")
        create_app(self.root, self.config)
        self.assertNotEqual(marker.read_text(), "previous-version")
        self.assertTrue(list((self.root / "web").glob("backup-*")))
        self.assertEqual(self.save("all", {"lockedSettings": ["themeMode"], "disabledFeatures": ["asr"]}, csrf).status_code, 200)
        self.assertEqual(self.client.get("/api/policy", headers=self.headers).json()["disabledFeatures"], ["asr"])

    def test_legacy_database_and_inherited_asr_secret(self):
        import sqlite3
        from contextlib import closing
        with closing(sqlite3.connect(self.root / "mdn.sqlite3")) as conn:
            conn.execute("ALTER TABLE devices DROP COLUMN leave_allowed")
            conn.commit()
        create_app(self.root, self.config)
        csrf = self.login()
        self.assertEqual(self.save("all", {"settings": {"cloudApiKey": "inherited-secret"}}, csrf).status_code, 200)
        self.assertEqual(self.save(self.identifier, {"hideAsrConfiguration": True}, csrf).status_code, 200)
        self.assertTrue(self.client.get("/api/policy", headers=self.headers).json()["hideAsrConfiguration"])
        self.assertNotIn("inherited-secret", self.client.get("/admin", params={"scope": self.identifier}).text)

    def test_selector_loop_handles_connection_reset(self):
        import asyncio
        from uvicorn import Config
        loop = Config("unused", loop=MDN_LOOP).get_loop_factory()()
        try:
            self.assertIsInstance(loop, asyncio.SelectorEventLoop)
        finally:
            loop.close()

    def test_global_and_device_policies_and_secret_retention(self):
        csrf = self.login()
        global_policy = {"settings": {"themeMode": "dark", "cloudApiKey": "secret-api-key", "cloudModel": "asr-model"}, "hideAsrConfiguration": True, "disabledFeatures": ["mcp"], "lockAllSettings": True}
        self.assertEqual(self.save("all", global_policy, csrf).status_code, 200)
        self.assertNotIn("secret-api-key", self.client.get("/admin").text)
        edited = dict(global_policy, settings={"themeMode": "dark", "cloudApiKey": "__KEEP_SECRET__"})
        self.assertEqual(self.save("all", edited, csrf).status_code, 200)
        self.assertEqual(self.save(self.identifier, {"settings": {"themeMode": "light"}, "lockAllSettings": False}, csrf).status_code, 200)
        policy = self.client.get("/api/policy", headers=self.headers).json()
        self.assertEqual(policy["settings"]["themeMode"], "light")
        self.assertEqual(policy["settings"]["cloudApiKey"], "secret-api-key")
        self.assertEqual(policy["disabledFeatures"], ["mcp"])
        self.assertFalse(policy["lockAllSettings"])
        self.assertNotIn(b"secret-api-key", (self.root / "mdn.sqlite3").read_bytes())
        self.assertEqual(self.save("all", {"settings": {"unknown": True}}, csrf).status_code, 422)
        self.assertEqual(self.save("all", {"lockAllSettings": True}, "wrong").status_code, 403)

    def test_signed_updates_and_enrollment(self):
        csrf = self.login()
        fields = {"form_token": csrf, "scope": "all", "mode": "ota", "version": "2.0.2", "url": "https://updates.example.edu/il.exe", "sha256": "a" * 64, "size": 1024}
        self.assertEqual(self.client.post("/admin/update", data=fields).status_code, 200)
        update = self.client.get("/api/policy", headers=self.headers).json()["update"]
        self.private.public_key().verify(base64.b64decode(update["signature"]), canonical(update), padding.PKCS1v15(), hashes.SHA256())
        self.assertIn("update_public_key:", self.client.get("/admin/enrollment.yaml").text)
        self.assertEqual(self.save("all", {"settings": {"themeMode": "dark"}}, csrf).status_code, 200)
        self.assertEqual(self.client.get("/api/policy", headers=self.headers).json()["update"]["id"], update["id"])
        self.client.post("/admin/update/cancel", data={"scope": "all", "form_token": csrf})
        self.assertIsNone(self.client.get("/api/policy", headers=self.headers).json()["update"])

    def test_telemetry_allowlist_and_idempotence(self):
        event = {"id": "asr-1", "kind": "asr", "appVersion": "2.0.1", "occurredAt": "2026-10-12T00:00:00Z", "metrics": {"success": True, "durationMs": 240}}
        for _ in range(2):
            self.assertEqual(self.client.post("/api/telemetry", headers=self.headers, json=event).status_code, 200)
        event["metrics"]["apiKey"] = "must-not-be-sent"
        self.assertEqual(self.client.post("/api/telemetry", headers=self.headers, json=event).status_code, 422)
        import sqlite3
        from contextlib import closing
        with closing(sqlite3.connect(self.root / "mdn.sqlite3")) as connection:
            self.assertEqual(connection.execute("SELECT count(*) FROM events").fetchone()[0], 1)

    def test_first_run_expands_assets_and_keeps_existing_identity(self):
        bootstrap=self.root/"bootstrap"
        config=initialize(bootstrap,"Test school","https://127.0.0.1:8443/","test-password-123",SecretStore())
        self.assertTrue((bootstrap/"web/templates/admin.html").is_file())
        self.assertTrue((bootstrap/"tls/server.crt").is_file())
        self.assertTrue((bootstrap/"server.json").is_file())
        self.assertEqual(len(config["certificate_sha256"]),64)
        encrypted=(bootstrap/"config.bin").read_bytes()
        with self.assertRaises(ValueError):initialize(bootstrap,"Other school","https://127.0.0.1:8443/","test-password-123",SecretStore())
        self.assertEqual(encrypted,(bootstrap/"config.bin").read_bytes())

    def test_dpapi_roundtrip(self):
        store = SecretStore()
        value = {"credential": "private-test-value"}
        encrypted = store.encode(value)
        self.assertNotIn(b"private-test-value", encrypted)
        self.assertEqual(store.decode(encrypted), value)

    def test_http_mode_login_enrollment_policy_and_updates(self):
        config = apply_transport(self.config, {}, True)
        with TestClient(create_app(self.root, config), base_url="http://mdn.example.edu") as http:
            page = http.get("/").text
            csrf = re.search(r'name="form_token" value="([^"]+)"', page)[1]
            self.assertNotIn("secure", http.cookies.get("session", ""))
            self.assertEqual(http.post("/login", data={"password": "test-password-123", "form_token": csrf}).status_code, 200)
            page = http.get("/admin").text
            csrf = re.search(r'name="form_token" value="([^"]+)"', page)[1]
            enrollment = http.get("/admin/enrollment.yaml").text
            self.assertIn('disable_ssl: true', enrollment)
            self.assertIn('server_url: "http://', enrollment)
            self.assertIn('server_certificate_sha256: ""', enrollment)
            for value in (True, False):
                self.assertEqual(http.post("/admin/policy", data={"scope": "all", "policy_json": json.dumps({"settings": {"fileAssociationEnabled": value}}), "form_token": csrf}).status_code, 200)
                self.assertEqual(http.get("/api/policy", headers=self.headers).json()["settings"]["fileAssociationEnabled"], value)
            self.assertEqual(http.post("/admin/update", data={"scope": "all", "mode": "cold", "version": "2.0.2", "url": "http://mdn.example.edu/setup.exe", "sha256": "a"*64, "size": 4, "form_token": csrf}).status_code, 200)

    def test_transport_configuration_and_cli_override(self):
        self.assertFalse(apply_transport(self.config, {})["disable_ssl"])
        self.assertTrue(apply_transport(self.config, {"disable_ssl": True})["disable_ssl"])
        self.assertTrue(apply_transport(self.config, {"disable_ssl": False}, True)["disable_ssl"])
        with self.assertRaises(ValueError): apply_transport(self.config, {"disable_ssl": "false"})

    def test_management_lock_global_and_device_override(self):
        csrf = self.login()
        self.assertEqual(self.save("all", {"lockManagement": True}, csrf).status_code, 200)
        self.assertTrue(self.client.get("/api/policy", headers=self.headers).json()["lockManagement"])
        self.assertEqual(self.save(self.identifier, {"lockManagement": False}, csrf).status_code, 200)
        self.assertFalse(self.client.get("/api/policy", headers=self.headers).json()["lockManagement"])
        self.assertEqual(self.save("all", {"lockManagement": "true"}, csrf).status_code, 422)


    def lesson_zip(self, version=1, corrupt=False):
        audio, transcript = b'RIFF-test-audio', b'1\n00:00:00,000 --> 00:00:01,000\nHello world.\n'
        manifest = {"formatVersion": 3, "title": "组织精听课程", "packageUuid": str(uuid.uuid4()), "packageVersion": version, "audioPath": "audio.wav", "transcriptPath": "transcript.srt", "durationMs": 1000, "sha256": {"audio.wav": hashlib.sha256(audio).hexdigest(), "transcript.srt": hashlib.sha256(transcript).hexdigest()}}
        body = io.BytesIO()
        with zipfile.ZipFile(body, "w") as archive:
            archive.writestr("manifest.json", json.dumps(manifest, ensure_ascii=False))
            archive.writestr("audio.wav", b'corrupt' if corrupt else audio)
            archive.writestr("transcript.srt", transcript)
        return body.getvalue(), manifest

    def upload_lesson(self, csrf, scopes="all", body=None):
        body = body if body is not None else self.lesson_zip()[0]
        return self.client.post("/admin/lessons/upload", params={"scopes": scopes, "filename": "course.ilp"}, content=body, headers={"X-CSRF-Token": csrf, "Content-Type": "application/octet-stream"})

    def test_lesson_upload_auth_scoping_signature_receipts_and_withdraw(self):
        from lesson_distribution import lesson_content
        csrf = self.login(); second = str(uuid.uuid4()); second_token = self.register(second)
        third = str(uuid.uuid4()); third_token = self.register(third)
        body, manifest = self.lesson_zip()
        self.assertEqual(self.upload_lesson("invalid", body=body).status_code, 403)
        self.assertEqual(self.upload_lesson(csrf, self.identifier + "," + second, body).status_code, 200)
        policy = self.client.get("/api/policy", headers=self.headers).json()
        item = policy["lessons"][0]
        self.assertEqual(item["packageUuid"], manifest["packageUuid"])
        self.private.public_key().verify(base64.b64decode(item["signature"]), lesson_content(item), padding.PKCS1v15(), hashes.SHA256())
        url = "/api/lessons/" + item["id"]
        self.assertEqual(self.client.get(url).status_code, 401)
        self.assertEqual(self.client.get(url, headers={"Authorization":"Bearer " + third_token}).status_code, 403)
        self.assertEqual(self.client.get(url, headers={"Authorization":"Bearer " + second_token}).content, body)
        self.assertEqual(self.client.post(url + "/receipt", headers=self.headers, json={"status":"imported","message":"已导入"}).status_code, 200)
        self.assertIn("导入完成", self.client.get("/admin?view=lessons").text)
        self.assertEqual(self.client.post(url + "/receipt", headers={"Authorization":"Bearer " + third_token}, json={"status":"imported"}).status_code, 403)
        self.assertEqual(self.client.post(url + "/receipt", headers=self.headers, json={"status":"invalid"}).status_code, 422)
        self.assertEqual(self.client.post("/admin/lessons/withdraw", data={"lesson_id":item["id"],"form_token":csrf}).status_code, 200)
        self.assertEqual(self.client.get(url, headers=self.headers).status_code, 403)
        self.assertEqual(self.client.get("/api/policy", headers=self.headers).json()["lessons"], [])

    def test_lesson_global_distribution_reaches_future_registration_and_realtime_push(self):
        with self.client:
            csrf = self.login()
            with self.client.websocket_connect("/api/connect", headers=self.headers) as ws:
                self.assertEqual(ws.receive_json()["type"], "ready")
                self.assertEqual(self.upload_lesson(csrf).status_code, 200)
                self.assertEqual(ws.receive_json()["type"], "policy")
            future = str(uuid.uuid4()); token = self.register(future)
            self.assertEqual(len(self.client.get("/api/policy", headers={"Authorization":"Bearer " + token}).json()["lessons"]), 1)

    def test_lesson_rejects_invalid_packages_and_targets_without_leaving_files(self):
        csrf = self.login()
        for body in (b'not-a-zip', self.lesson_zip(corrupt=True)[0]):
            self.assertEqual(self.upload_lesson(csrf, body=body).status_code, 422)
        self.assertEqual(self.upload_lesson(csrf, "all," + self.identifier).status_code, 422)
        self.assertEqual(self.upload_lesson(csrf, str(uuid.uuid4())).status_code, 404)
        self.assertEqual(list((self.root / "lessons").iterdir()), [])

    def test_blur_policy_accepts_range_and_locks_shared_setting(self):
        csrf = self.login()
        for value in (1, 8):
            self.assertEqual(self.save("all", {"settings":{"transcriptBlurIntensity":value}}, csrf).status_code, 200)
            self.assertEqual(self.client.get("/api/policy", headers=self.headers).json()["settings"]["transcriptBlurIntensity"], value)
        for value in (0, 9):
            self.assertEqual(self.save("all", {"settings":{"transcriptBlurIntensity":value}}, csrf).status_code, 422)
        legacy = self.client.get("/api/policy", headers={"Authorization":"Bearer " + self.token}).json()
        self.assertNotIn("transcriptBlurIntensity", legacy["settings"])

if __name__ == "__main__":
    unittest.main()
