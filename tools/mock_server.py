"""In-memory stand-in for the NDS Station server, for developing the desktop app.

Implements the same endpoints the app uses. Accounts:
  admin / admin123   (administrator)
  mario / temp123    (must change password on first login)

Usage: python mock_server.py [port]   (default 3000)
"""
import base64
import json
import re
import sys
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlparse

sys.path.insert(0, str(Path(__file__).parent))
import make_test_rom  # noqa: E402

lock = threading.Lock()
users = {
    1: {"id": 1, "username": "admin", "password": "admin123", "is_admin": True, "must_change_password": False},
    2: {"id": 2, "username": "mario", "password": "temp123", "is_admin": False, "must_change_password": True},
}
games = {
    "1": {"id": 1, "name": "Test ROM (magenta)", "rom": make_test_rom.build(0x7C1F)},
    "2": {"id": 2, "name": "Test ROM (verde)", "rom": make_test_rom.build(0x03E0)},
    "3": {"id": 3, "name": "Gioco senza ROM", "rom": None},
}
saves = {}  # (user_id, game_id) -> bytes
consoles = {}  # user_id -> firmware bytes


def make_token(user):
    payload = base64.urlsafe_b64encode(json.dumps(
        {"id": user["id"], "username": user["username"], "is_admin": user["is_admin"]}).encode()).decode().rstrip("=")
    return f"eyJhbGciOiJub25lIn0.{payload}.mock{user['id']}"


def user_from_token(token):
    m = re.match(r".*\.mock(\d+)$", token or "")
    return users.get(int(m.group(1))) if m else None


def parse_multipart(body, content_type):
    boundary = content_type.split("boundary=")[1].strip('"').encode()
    fields = {}
    for part in body.split(b"--" + boundary):
        if b"\r\n\r\n" not in part:
            continue
        head, value = part.split(b"\r\n\r\n", 1)
        name = re.search(rb'[; ]name="?([^";\r\n]+)"?', head)  # quoted or bare token
        if name:
            if b'name="' not in head:
                print(f"  warning: unquoted multipart name {name.group(1).decode()!r}")
            fields[name.group(1).decode()] = value[:-2] if value.endswith(b"\r\n") else value
    return fields


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"   # keep-alive like uvicorn; every response sets Content-Length
    def _send(self, status, payload=None, raw=None, content_type="application/json"):
        data = raw if raw is not None else json.dumps(payload).encode()
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def _send_empty(self, status):
        self.send_response(status)
        self.send_header("Content-Length", "0")
        self.end_headers()

    def _body(self):
        length = int(self.headers.get("Content-Length", 0))
        return self.rfile.read(length) if length else b""

    def _user(self):
        auth = self.headers.get("Authorization", "")
        return user_from_token(auth[7:]) if auth.startswith("Bearer ") else None

    def _route(self, method):
        url = urlparse(self.path)
        path, query = url.path, parse_qs(url.query)

        if method == "POST" and path == "/auth/login":
            body = json.loads(self._body() or b"{}")
            with lock:
                user = next((u for u in users.values() if u["username"] == body.get("username")), None)
            if not user or user["password"] != body.get("password"):
                return self._send(200, [{"error": "Invalid username or password"}, 401])
            return self._send(200, {"token": make_token(user), "isAdmin": user["is_admin"],
                                    "mustChangePassword": user["must_change_password"]})

        user = self._user()
        if not user:
            return self._send(401, {"error": "Unauthorized"})

        if method == "POST" and path == "/auth/change-password":
            body = json.loads(self._body() or b"{}")
            if len(body.get("new_password", "")) < 6:
                return self._send(400, {"error": "Password troppo corta"})
            with lock:
                user["password"] = body["new_password"]
                user["must_change_password"] = False
            return self._send(200, {"ok": True})

        if method == "GET" and path == "/games":
            with lock:
                return self._send(200, [{"id": g["id"], "name": g["name"], "url": f"/roms/{gid}.nds"}
                                        for gid, g in games.items()])

        m = re.match(r"^/roms/(.+)\.nds$", path)
        if method == "GET" and m:
            game = games.get(m.group(1))
            if not game or not game["rom"]:
                return self._send(404, {"error": "ROM non trovata"})
            return self._send(200, raw=game["rom"], content_type="application/octet-stream")

        if method == "GET" and path == "/saves":
            data = saves.get((user["id"], query.get("gameId", [""])[0]))
            if data is None:
                return self._send_empty(204)
            return self._send(200, raw=data, content_type="application/octet-stream")

        if method == "POST" and path == "/saves":
            fields = parse_multipart(self._body(), self.headers["Content-Type"])
            saves[(user["id"], fields["gameId"].decode())] = fields["saveFile"]
            print(f"  save stored: user={user['username']} game={fields['gameId'].decode()} bytes={len(fields['saveFile'])}")
            return self._send(200, {"ok": True})

        if method == "GET" and path == "/console":
            data = consoles.get(user["id"])
            if data is None:
                return self._send_empty(204)
            return self._send(200, raw=data, content_type="application/octet-stream")

        if method == "POST" and path == "/console":
            fields = parse_multipart(self._body(), self.headers["Content-Type"])
            data = fields.get("firmware", b"")
            if len(data) not in (128 * 1024, 256 * 1024, 512 * 1024):
                return self._send(400, {"detail": "Invalid firmware size"})
            consoles[user["id"]] = data
            print(f"  console stored: user={user['username']} bytes={len(data)}")
            return self._send(200, {"success": True})

        if method == "GET" and path == "/users/search":
            q = query.get("q", [""])[0].lower()
            return self._send(200, [{"id": u["id"], "username": u["username"]} for u in users.values() if q in u["username"]])

        if not user["is_admin"]:
            return self._send(403, {"error": "Solo per amministratori"})

        if method == "GET" and path == "/admin/users":
            with lock:
                return self._send(200, {"users": [{k: v for k, v in u.items() if k != "password"} for u in users.values()]})

        if method == "POST" and path == "/auth/register":
            body = json.loads(self._body() or b"{}")
            with lock:
                if any(u["username"] == body.get("username") for u in users.values()):
                    return self._send(409, {"error": "Utente già esistente"})
                uid = max(users) + 1
                users[uid] = {"id": uid, "username": body["username"], "password": body["password"],
                              "is_admin": False, "must_change_password": True}
            return self._send(201, {"id": uid})

        m = re.match(r"^/admin/users/(\d+)(/reset-password)?$", path)
        if m and method == "DELETE" and not m.group(2):
            with lock:
                users.pop(int(m.group(1)), None)
            return self._send(200, {"ok": True})
        if m and method == "POST" and m.group(2):
            body = json.loads(self._body() or b"{}")
            with lock:
                target = users.get(int(m.group(1)))
                if not target:
                    return self._send(404, {"error": "Utente non trovato"})
                target["password"] = body["new_password"]
                target["must_change_password"] = bool(body.get("must_change_password", True))
            return self._send(200, {"ok": True})

        if method == "POST" and path == "/admin/games":
            if "multipart/form-data" not in self.headers.get("Content-Type", ""):
                return self._send(422, {"detail": "title form field required"})
            title = parse_multipart(self._body(), self.headers["Content-Type"]).get("title", b"").decode()
            with lock:
                gid = str(max(int(k) for k in games) + 1)
                games[gid] = {"id": int(gid), "name": title, "rom": None}
            return self._send(201, {"id": int(gid)})

        if method == "POST" and path == "/games":
            fields = parse_multipart(self._body(), self.headers["Content-Type"])
            with lock:
                gid = str(max(int(k) for k in games) + 1)
                games[gid] = {"id": int(gid), "name": fields["gameName"].decode(), "rom": fields["ndsFile"]}
            return self._send(201, {"id": int(gid)})

        return self._send(404, {"error": f"{method} {path} non gestito"})

    def do_GET(self):
        self._route("GET")

    def do_POST(self):
        self._route("POST")

    def do_DELETE(self):
        self._route("DELETE")


if __name__ == "__main__":
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 3000
    print(f"mock server on http://localhost:{port}")
    ThreadingHTTPServer(("127.0.0.1", port), Handler).serve_forever()
