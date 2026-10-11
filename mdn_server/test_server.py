import base64
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
from server import SecretStore, canonical, create_app, password_hash


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
        self.headers = {"Authorization": "Bearer " + self.token}

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

    def test_dpapi_roundtrip(self):
        store = SecretStore()
        value = {"credential": "private-test-value"}
        encrypted = store.encode(value)
        self.assertNotIn(b"private-test-value", encrypted)
        self.assertEqual(store.decode(encrypted), value)


if __name__ == "__main__":
    unittest.main()
