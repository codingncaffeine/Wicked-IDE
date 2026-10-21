#!/usr/bin/env python3
"""A stand-in for llama-server in router mode: just enough of its model-management and chat API for the tests.

Behaviour is scripted by fake.json next to this file (re-read on every request) and everything it is asked is appended to
fake-log.jsonl beside it, so a test can assert on what the runtime sent. Standard library only."""
import json
import os
import queue
import signal
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

HERE = os.path.dirname(os.path.abspath(__file__))
LOG = os.path.join(HERE, "fake-log.jsonl")
LOCK = threading.Lock()
MODELS = {}
SUBS = []
ARGS = {}
PRESET = ""


def script():
    try:
        with open(os.path.join(HERE, "fake.json"), encoding="utf-8") as f:
            return json.load(f)
    except (OSError, ValueError):
        return {}


def log(**entry):
    with LOCK:
        with open(LOG, "a", encoding="utf-8") as f:
            f.write(json.dumps(entry) + "\n")


def read_preset():
    global PRESET
    sections = {}
    current = None
    with open(ARGS["models-preset"], encoding="utf-8") as f:
        PRESET = f.read()
    for raw in PRESET.splitlines():
        line = raw.strip()
        if not line or line.startswith(";") or line.startswith("version"):
            continue
        if line.startswith("[") and line.endswith("]"):
            current = line[1:-1]
            sections[current] = {}
        elif current is not None and "=" in line:
            k, v = line.split("=", 1)
            sections[current][k.strip()] = v.strip()
    glob = sections.pop("*", {})
    return {name: {**glob, **values} for name, values in sections.items()}


def sync_models():
    sections = read_preset()
    with LOCK:
        for name, values in sections.items():
            if name not in MODELS:
                MODELS[name] = {"status": "unloaded", "failed": False, "exit": None, "preset": values}
            else:
                MODELS[name]["preset"] = values
        for name in list(MODELS):
            if name not in sections:
                del MODELS[name]


def publish(model, event, data=None):
    payload = {"model": model, "event": event}
    if data is not None:
        payload["data"] = data
    for q in list(SUBS):
        q.put(payload)


def model_json(name, m):
    status = {"value": m["status"], "args": ["llama-server"] + (["--ctx-size", m["preset"]["c"]] if "c" in m["preset"] else [])}
    if m["failed"]:
        status["failed"] = True
        status["exit_code"] = m["exit"]
    modalities = ["text", "image"] if "mmproj" in m["preset"] else ["text"]
    return {"id": name, "path": m["preset"].get("model"), "status": status, "architecture": {"input_modalities": modalities, "output_modalities": ["text"]}}


def do_load(name):
    s = script()
    with LOCK:
        m = MODELS[name]
        m["status"] = "loading"
        m["failed"] = False
    publish(name, "model_status", {"status": "loading"})
    ms = s.get("loadMs", 20) / 1000.0
    time.sleep(ms / 2)
    publish(name, "model_status", {"status": "loading", "progress": {"stages": ["text_model"], "current": "text_model", "value": 0.5}})
    time.sleep(ms / 2)
    if name in s.get("failLoad", []):
        sys.stderr.write("[%d] 0.00.000.100 E llama_model_load: error loading model: fake failure for %s\n" % (os.getpid() + 1, name))
        sys.stderr.write("0.00.000.200 I srv           run: instance name=%s exited with status 1\n" % name)
        sys.stderr.flush()
        with LOCK:
            m["status"] = "unloaded"
            m["failed"] = True
            m["exit"] = 1
        publish(name, "model_status", {"status": "unloaded"})
        return
    with LOCK:
        m["status"] = "loaded"
    publish(name, "model_status", {"status": "loaded", "info": {}})


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"  # keep-alive, as the real server

    def log_message(self, *a):
        pass

    def send_json(self, code, obj):
        body = json.dumps(obj).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def body_json(self):
        n = int(self.headers.get("Content-Length", "0"))
        return json.loads(self.rfile.read(n) or b"{}")

    def do_GET(self):
        u = urlparse(self.path)
        if u.path == "/health":
            self.send_json(200, {"status": "ok"})
        elif u.path == "/models":
            if parse_qs(u.query).get("reload"):
                log(t="reload")
                sync_models()
                with LOCK:
                    for name, m in MODELS.items():
                        pass
            with LOCK:
                data = [model_json(n, m) for n, m in MODELS.items()]
            self.send_json(200, {"data": data})
        elif u.path == "/props":
            name = parse_qs(u.query).get("model", [""])[0]
            with LOCK:
                m = MODELS.get(name)
            n_ctx = int(m["preset"].get("c", "4096")) if m else 4096
            self.send_json(200, {"default_generation_settings": {"n_ctx": n_ctx}})
        elif u.path == "/models/sse":
            q = queue.Queue()
            SUBS.append(q)
            self.send_response(200)
            self.send_header("Content-Type", "text/event-stream")
            self.send_header("Connection", "close")
            self.end_headers()
            self.close_connection = True
            try:
                self.wfile.write(b": connected\n\n")
                self.wfile.flush()
                while True:
                    try:
                        ev = q.get(timeout=0.5)
                    except queue.Empty:
                        self.wfile.write(b": keep-alive\n\n")
                        self.wfile.flush()
                        continue
                    self.wfile.write(("data: " + json.dumps(ev) + "\n\n").encode())
                    self.wfile.flush()
            except (BrokenPipeError, ConnectionResetError):
                pass
            finally:
                SUBS.remove(q)
        else:
            self.send_json(404, {"error": {"message": "not found", "code": 404}})

    def do_POST(self):
        u = urlparse(self.path)
        if u.path in ("/models/load", "/models/unload"):
            name = self.body_json().get("model")
            with LOCK:
                known = name in MODELS
            if not known:
                self.send_json(400, {"error": {"code": 400, "message": "model not found", "type": "invalid_request_error"}})
                return
            if u.path == "/models/load":
                log(t="load", model=name)
                threading.Thread(target=do_load, args=(name,), daemon=True).start()
            else:
                log(t="unload", model=name)
                with LOCK:
                    MODELS[name]["status"] = "unloaded"
                publish(name, "model_status", {"status": "unloaded"})
            self.send_json(200, {"success": True})
        elif u.path == "/v1/chat/completions":
            self.chat()
        else:
            self.send_json(404, {"error": {"message": "not found", "code": 404}})

    def chat(self):
        body = self.body_json()
        log(t="chat", body=body)
        s = script()
        with LOCK:
            m = MODELS.get(body.get("model"))
            loaded = m is not None and m["status"] == "loaded"
        if not loaded:
            self.send_json(400, {"error": {"code": 400, "message": "model '%s' is not loaded" % body.get("model"), "type": "invalid_request_error"}})
            return
        if "chatStatus" in s:
            self.send_json(s["chatStatus"], s.get("chatBody", {}))
            return
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Connection", "close")
        self.end_headers()
        self.close_connection = True
        delay = s.get("chatDelayMs", 0) / 1000.0
        try:
            for item in s.get("chat", ["[DONE]"]):
                if item == "@crash":
                    os._exit(7)
                if item == "@hangup":
                    self.wfile.flush()
                    self.connection.close()
                    return
                raw = item if isinstance(item, str) else json.dumps(item)
                self.wfile.write(("data: " + raw + "\n\n").encode())
                self.wfile.flush()
                if delay:
                    time.sleep(delay)
        except (BrokenPipeError, ConnectionResetError):
            log(t="chat-aborted")


def main():
    argv = sys.argv[1:]
    i = 0
    while i < len(argv):
        if argv[i].startswith("--"):
            if i + 1 < len(argv) and not argv[i + 1].startswith("--"):
                ARGS[argv[i][2:]] = argv[i + 1]
                i += 1
            else:
                ARGS[argv[i][2:]] = True
        i += 1
    sync_models()
    log(t="start", args=argv, preset=PRESET, pid=os.getpid())
    signal.signal(signal.SIGTERM, lambda *_: os._exit(0))
    server = ThreadingHTTPServer(("127.0.0.1", int(ARGS["port"])), Handler)
    server.daemon_threads = True
    sys.stderr.write("fake llama-server listening on %s\n" % ARGS["port"])
    sys.stderr.flush()
    server.serve_forever()


if __name__ == "__main__":
    main()
