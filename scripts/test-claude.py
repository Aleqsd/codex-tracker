"""Exercise the published Claude collectors over stdio with isolated fictional files."""
import argparse
import base64
import json
import os
from pathlib import Path
import subprocess
import tempfile
import time
import uuid

parser = argparse.ArgumentParser()
parser.add_argument("exe")
args = parser.parse_args()
executable = Path(args.exe).resolve(strict=True)
scratch = Path(__file__).resolve().parent.parent / "artifacts" / "claude-protocol"
scratch.mkdir(parents=True, exist_ok=True)
checks = 0


def check(value, label):
    global checks
    if not value:
        raise AssertionError(label)
    checks += 1
    print("OK", label)


with tempfile.TemporaryDirectory(dir=scratch) as temporary:
    root = Path(temporary)
    config = root / "Claude config '$` [fixture]"
    config.mkdir()
    data = root / "Tracker data '$` [fixture]"
    environment = dict(os.environ, CLAUDE_CONFIG_DIR=str(config))
    secret = "FICTIONAL-CLAUDE-SECRET-NEVER-PERSIST"
    private = "FICTIONAL-PRIVATE-TRANSCRIPT-NEVER-PERSIST"

    def sign_in(email, account_id, expires=None):
        (config / ".claude.json").write_text(json.dumps({"oauthAccount": {
            "emailAddress": email, "accountUuid": str(account_id),
            "organizationUuid": "44444444-4444-4444-4444-444444444444"}}), encoding="utf-8")
        (config / ".credentials.json").write_text(json.dumps({"claudeAiOauth": {
            "accessToken": secret, "refreshToken": secret, "subscriptionType": "max",
            "rateLimitTier": "default_claude_max_20x", "expiresAt": expires or int((time.time() + 3600) * 1000)}}), encoding="utf-8")

    def invoke(mode, payload, powershell=False):
        arguments = [str(executable), mode, "--claude-data-directory", str(data)]
        if powershell:
            # Identical transport to the copied Claude settings, with an isolated data-directory argument.
            quoted_exe = executable.as_posix().replace("'", "''")
            quoted_data = data.as_posix().replace("'", "''")
            script = "$ProgressPreference = 'SilentlyContinue'; $OutputEncoding = [Console]::InputEncoding = [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); $input | & '" + quoted_exe + "' " + mode + " --claude-data-directory '" + quoted_data + "'"
            encoded = base64.b64encode(script.encode("utf-16le")).decode("ascii")
            arguments = ["powershell", "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded]
        result = subprocess.run(arguments, input=payload.encode("utf-8"), stdout=subprocess.PIPE,
                                stderr=subprocess.PIPE, env=environment, timeout=12,
                                creationflags=subprocess.CREATE_NO_WINDOW)
        if result.returncode != 0 or result.stderr:
            diagnostic = result.stderr.decode("utf-8", errors="replace").replace(secret, "[fixture]").replace(private, "[fixture]")
            raise AssertionError(f"collector returned {result.returncode}: {diagnostic}")
        check(result.returncode == 0 and result.stderr == b"", "collector exits successfully without error output")
        return result.stdout.decode("utf-8").strip()

    def payload(session):
        return json.dumps({"session_id": str(session), "source": "startup", "transcript_path": private,
                           "cwd": private, "rate_limits": {
                               "five_hour": {"used_percentage": 42, "resets_at": int(time.time() + 3600)},
                               "seven_day": {"used_percentage": 70, "resets_at": int(time.time() + 86400)}}})

    first = uuid.uuid4()
    session = uuid.uuid4()
    sign_in("demo@example.test", first)
    native_before = {p.name: p.read_bytes() for p in config.iterdir()}
    check(invoke("--claude-statusline", payload(session)) == "", "unbound session does not invent quota")
    check(invoke("--claude-session-start", payload(session), powershell=True) == "", "PowerShell SessionStart is silent")
    line = invoke("--claude-statusline", payload(session), powershell=True)
    check("58% restant" in line and "30% restant" in line, "PowerShell statusline forwards UTF-8 stdin and both quota windows")
    check(native_before == {p.name: p.read_bytes() for p in config.iterdir()}, "native Claude files remain unchanged")
    captures = list((data / "claude-observations").glob("account-*.json"))
    check(len(captures) == 1, "one sanitized account observation is written")
    stored = "".join(p.read_text(encoding="utf-8") for p in (data / "claude-observations").glob("*.json"))
    check(secret not in stored and private not in stored and "transcript" not in stored, "credentials and conversation data are absent from captures")
    check(set(p.name for p in data.iterdir()) == {"claude-observations"}, "collectors do not write tracker profiles or preferences")
    check(invoke("--claude-statusline", "{broken " + secret) == "", "malformed native input is silent")
    sign_in("second@example.test", uuid.uuid4())
    invoke("--claude-session-start", payload(session))
    check(invoke("--claude-statusline", payload(session)) == "", "an old session cannot be rebound after an account switch")
    session = uuid.uuid4()
    invoke("--claude-session-start", payload(session))
    check("58% restant" in invoke("--claude-statusline", payload(session)), "a new session records the newly active account")
    check(len(list((data / "claude-observations").glob("account-*.json"))) == 2, "previous accounts keep independent observations")
    sign_in("expired@example.test", uuid.uuid4(), int((time.time() - 60) * 1000))
    check(invoke("--claude-session-start", payload(uuid.uuid4())) == "", "expired authentication does not collect quota")

print(f"{checks} Claude collector process checks passed")
