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

    def test_credentials_and_device_revocation(self):
        self.assertEqual(self.client.get("/api/policy").status_code, 401)
        payload = {"deviceId": self.identifier, "deviceName": "impersonator", "appVersion": "2.0.1", "enrollmentToken": self.config["enrollment_token"]}
        self.assertEqual(self.client.post("/api/register", json=payload).status_code, 409)
        csrf = self.login()
        self.client.post("/admin/device", data={"device_id": self.identifier, "action": "disable", "form_token": csrf})
        self.assertEqual(self.client.get("/api/policy", headers=self.headers).status_code, 403)

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
