"""Exercise the actual published WinExe over stdio. Fictional data, no network sends."""
import argparse
import json
import queue
import subprocess
import threading
import uuid
import time

parser = argparse.ArgumentParser()
parser.add_argument("exe")
args = parser.parse_args()
instance = str(uuid.uuid4())
base = [args.exe, "--demo", "--demo-instance", instance]
clients = []
checks = 0

def check(value, label):
    global checks
    if not value:
        raise AssertionError(label)
    checks += 1
    print("OK", label)

class Client:
    def __init__(self):
        self.p = subprocess.Popen(base + ["--mcp"], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                  stderr=subprocess.PIPE, text=True, encoding="utf-8", creationflags=subprocess.CREATE_NO_WINDOW)
        clients.append(self)
        self.q = queue.Queue()
        self.errors = []
        def read_errors():
            for line in self.p.stderr: self.errors.append(line)
        threading.Thread(target=read_errors, daemon=True).start()
        self.seq = 0
        def read():
            for line in self.p.stdout:
                try: self.q.put(json.loads(line))
                except Exception: self.q.put({"unexpected_stdout": line})
            self.q.put({"eof": True})
        threading.Thread(target=read, daemon=True).start()
        self.rpc("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "tracker-smoke", "version": "1.0"}})
        self.send({"jsonrpc": "2.0", "method": "notifications/initialized"})
    def send(self, message):
        self.p.stdin.write(json.dumps(message) + "\n")
        self.p.stdin.flush()
    def rpc(self, method, params):
        self.seq += 1
        self.send({"jsonrpc": "2.0", "id": self.seq, "method": method, "params": params})
        deadline = time.monotonic() + 28
        while True:
            try: item = self.q.get(timeout=max(.01, deadline - time.monotonic()))
            except queue.Empty: raise RuntimeError("MCP timeout: " + "".join(self.errors))
            if "unexpected_stdout" in item or "eof" in item: raise RuntimeError(str(item) + "".join(self.errors))
            if item.get("id") == self.seq:
                if "error" in item: raise RuntimeError(item["error"])
                return item["result"]
    def tool(self, name, **arguments):
        r = self.rpc("tools/call", {"name": name, "arguments": arguments})
        if r.get("isError"): raise RuntimeError(r)
        return json.loads(next(c["text"] for c in r["content"] if c["type"] == "text"))

try:
    a = Client()
    names = {t["name"] for t in a.rpc("tools/list", {})["tools"]}
    check(len(names) == 16, "16 typed MCP tools discovered")
    status = a.tool("get_status")["result"]
    check(bool(status["revision"]), "cold background startup responds with revision")
    check(len(a.tool("list_accounts")["result"]["accounts"]) > 0, "fictional accounts readable")
    check(len(a.tool("list_resets")["result"]["resets"]) > 0, "reset types and dates readable")
    b = Client()
    check(b.tool("get_status")["result"]["connections"] == 2, "two clients share one tracker")
    pref = a.tool("get_preferences")["result"]
    updated = dict(pref["settings"], themeMode="Light")
    rid = str(uuid.uuid4())
    payload = dict(requestId=rid, expectedRevision=pref["revision"], settings=updated)
    check(a.tool("update_preferences", **payload)["result"]["status"] == "completed", "settings mutation succeeds")
    check(a.tool("update_preferences", **payload)["result"]["status"] == "completed", "same UUID returns existing receipt")
    conflict = b.tool("update_preferences", requestId=str(uuid.uuid4()), expectedRevision=pref["revision"], settings=updated)
    check(conflict.get("error") == "revision_conflict", "stale client cannot overwrite settings")
    # Demo suppresses all external sends, including approved requests. A pending request is cancelled via revision change.
    revision = a.tool("get_status")["result"]["revision"]
    testid = str(uuid.uuid4())
    test = a.tool("test_notification", requestId=testid, expectedRevision=revision, channel="Sms")["result"]
    check(test["status"] == "pending", "SMS test requires local confirmation")
    check(a.tool("test_notification", requestId=testid, expectedRevision=revision, channel="Sms")["result"]["requestId"] == testid, "repeated test does not enqueue twice")
    newer = a.tool("get_preferences")["result"]
    a.tool("update_preferences", requestId=str(uuid.uuid4()), expectedRevision=newer["revision"], settings=dict(newer["settings"], themeMode="Dark"))
    check(a.tool("get_action_status", requestId=testid)["result"]["status"] == "cancelled", "concurrent edit cancels pending confirmation")
    # Save a sentinel only to isolated demo data; assert no credential is returned.
    revision = a.tool("get_status")["result"]["revision"]
    key = "SG.FICTIONAL-SECRET-NOT-A-REAL-KEY"
    result = a.tool("configure_channel", requestId=str(uuid.uuid4()), expectedRevision=revision,
                    configuration={"provider": "sendgrid", "sendGrid": {"apiKey": key, "from": "demo@example.com", "to": "demo@example.org"}})
    check(result["result"]["status"] == "completed", "write-only credential stored without enabling channel")
    channels = a.tool("get_channels")
    check(key not in json.dumps(channels) and not channels["result"]["sendGrid"]["enabled"], "credential absent from readback; channel remains disabled")
    subprocess.run(base + ["--exit"], timeout=10, creationflags=subprocess.CREATE_NO_WINDOW, check=True)
    a.p.wait(timeout=15); b.p.wait(timeout=15)
    check(a.p.returncode == 0 and b.p.returncode == 0, "tracker shutdown releases both MCP processes for update")
    print(f"{checks} MCP process checks passed")
finally:
    subprocess.run(base + ["--exit"], timeout=10, creationflags=subprocess.CREATE_NO_WINDOW)
    for client in clients:
        if client.p.poll() is None:
            client.p.terminate()
            client.p.wait(timeout=5)
