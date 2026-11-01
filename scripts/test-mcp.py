"""Exercise the actual published WinExe over stdio. Fictional data, no network sends."""
import argparse
import json
import queue
import subprocess
import threading
import uuid
import time
from concurrent.futures import ThreadPoolExecutor

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
    check(len(names) == 51, "51 typed MCP tools discovered")
    google = a.tool("get_google_calendar")["result"]
    check("connected" in google and "reserves" in google and "token" not in json.dumps(google).lower(), "Google calendar state readable without tokens")
    status = a.tool("get_status")["result"]
    check(bool(status["revision"]), "cold background startup responds with revision")
    def cli(*command):
        r = subprocess.run(base + ["--cli", *command], capture_output=True, text=True, encoding="utf-8", timeout=45, creationflags=subprocess.CREATE_NO_WINDOW)
        return r.returncode, r.stdout
    code, out = cli("help")
    check(code == 0 and all(n in out for n in names), "CLI exposes every MCP tool by name")
    code, out = cli("get_status")
    check(code == 0 and json.loads(out)["result"]["revision"], "CLI reads status as JSON and exits")
    code, out = cli("test_notification", "channel=Windows")
    check(code == 0 and json.loads(out)["result"]["status"] in ("completed", "pending"), "CLI change fetches the revision itself")
    code, out = cli("open_page", "page=Nope")
    check(code == 1 and "error" in json.loads(out), "CLI reports refusals with exit code 1")
    accounts = a.tool("list_accounts")["result"]["accounts"]
    inactive = next(x for x in accounts if not x["isActive"])
    code, out = cli("rename_account", f"accountId={inactive['id']}", "name=Studio")
    check(code == 0 and json.loads(out)["result"]["status"] == "completed", "CLI renames an account")
    check(a.tool("get_preferences")["result"]["accountNames"].get(inactive["id"]) == "Studio", "account name readable back")
    code, out = cli("set_tray_quota", "provider=Codex", "window=Short")
    check(code == 0 and a.tool("get_preferences")["result"]["trayQuota"]["window"] == "Short", "tray quota chosen by command")
    code, out = cli("remove_account", f"accountId={accounts[0]['id'] if accounts[0]['isActive'] else inactive['id']}")
    check(code == 1 or json.loads(out)["result"]["status"] == "pending", "account removal is refused for the active account or waits for local confirmation")
    code, out = cli("unknown_command")
    check(code == 2, "CLI rejects unknown commands with exit code 2")
    code, out = cli("help")
    check(all(n in out for n in names), "CLI still lists every MCP tool")
    acct = a.tool("list_accounts")["result"]["accounts"][0]["id"]
    hist = a.tool("get_usage_history", accountId=acct, hours=168, window="Weekly")["result"]
    check(hist["window"] == "Weekly" and isinstance(hist["points"], list), "usage history readable")
    check("current" in a.tool("get_update_status")["result"], "update status readable")
    news = a.tool("get_whats_new")["result"]
    check("unseen" in news and len(news["all"]) > 0, "what's new readable")
    status = a.tool("get_status")["result"]
    check("notices" in status and "onboardingComplete" in status and "windowsNotifications" in status, "notices and health readable in status")
    check("calendarEvents" in a.tool("list_resets")["result"], "prefilled calendar links readable")
    result = a.tool("export_calendar_ics", requestId=str(uuid.uuid4()), expectedRevision=a.tool("get_status")["result"]["revision"])["result"]
    check(result["status"] == "completed" and "BEGIN:VCALENDAR" in result["detail"], "calendar exported as text without a file")
    code, out = cli("dismiss_whats_new")
    check(code == 0 and json.loads(out)["result"]["status"] == "completed", "CLI marks what's new as seen")
    check(not a.tool("get_whats_new")["result"]["unseen"], "nothing left to see after dismissal")
    code, out = cli("acknowledge_recovery")
    check(code == 1, "nothing to acknowledge is refused before any confirmation")
    check(len(a.tool("list_accounts")["result"]["accounts"]) > 0, "fictional accounts readable")
    check(len(a.tool("list_resets")["result"]["resets"]) > 0, "reset types and dates readable")
    b = Client()
    check(b.tool("get_status")["result"]["connections"] == 2, "two clients share one tracker")
    revision_before_navigation = a.tool("get_status")["result"]["revision"]
    with ThreadPoolExecutor(max_workers=2) as pool:
        opened = [pool.submit(client.tool, "open_page", page="Comptes") for client in (a, b)]
        check(all(request.result()["result"]["opened"] for request in opened), "two clients can open the same page concurrently")
    check(a.tool("open_page", page="Comptes")["result"]["opened"], "repeated navigation remains open")
    check(a.tool("open_page", page="unknown-fixture-page").get("error") == "invalid_or_unavailable", "unknown navigation is refused")
    check(a.tool("get_status")["result"]["revision"] == revision_before_navigation, "navigation preserves preference revision")
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
    topic = "codex-tracker-" + uuid.uuid4().hex[:20]
    result = a.tool("configure_channel", requestId=str(uuid.uuid4()), expectedRevision=a.tool("get_status")["result"]["revision"],
                    configuration={"provider": "ntfy", "ntfy": {"enabled": False, "server": "https://ntfy.sh", "topic": topic, "token": ""}})
    check(result["result"]["status"] == "completed", "ntfy topic stored without enabling the phone channel")
    channels = a.tool("get_channels")["result"]
    check(topic not in json.dumps(channels) and channels["ntfy"]["hasTopic"] and not channels["ntfy"]["enabled"], "ntfy topic is write-only and the channel stays off")
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
