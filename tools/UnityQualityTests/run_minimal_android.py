"""Collect the empty Unity Android control's complete, own-process startup/exit log."""
import argparse
from pathlib import Path
import subprocess
import time

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("project", type=Path)
parser.add_argument("--adb", type=Path, required=True)
parser.add_argument("--device", required=True)
parser.add_argument("--label", required=True)
args = parser.parse_args()
package = "com.crestwater.runtimeaudit.empty"
output = args.project / ("MinimalControl-" + args.label)
output.mkdir(exist_ok=True)

def adb(*command, check=True, timeout=30):
    result = subprocess.run([str(args.adb), "-s", args.device, *command],
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=timeout)
    if check and result.returncode:
        raise RuntimeError(result.stdout.decode("utf-8", errors="replace"))
    return result.stdout

print(adb("install", "-r", str(args.project / "MinimalPlayer/Empty.apk"), timeout=180).decode(), flush=True)
launch = adb("shell", "am", "start", "-W", "-n", package + "/com.unity3d.player.UnityPlayerActivity", timeout=60)
(output / "launch.txt").write_bytes(launch)
print(launch.decode(), flush=True)
pid = adb("shell", "pidof", package).decode().strip()
if not pid.isdigit():
    raise RuntimeError("Empty control process PID was not observed")
started = time.monotonic()
while time.monotonic() - started < 100:
    if not adb("shell", "pidof", package, check=False).strip():
        break
    time.sleep(2)
log = adb("logcat", "-d", "--pid=" + pid, "-s", "Unity:*", "AndroidRuntime:*", "libc:*")
(output / "logcat.txt").write_bytes(log)
lines = log.decode("utf-8", errors="replace").splitlines()
errors = [line for line in lines if " E Unity" in line or " E AndroidRuntime" in line or "Fatal signal" in line]
complete = b"MINIMAL_ANDROID_CONTROL_COMPLETE" in log
summary = "PID=" + pid + "; controlCompleted=" + str(complete) + "; fullLogErrors=" + str(len(errors))
(output / "results.txt").write_text(summary + "\n" + "\n".join(errors), encoding="utf-8")
print(summary, flush=True)
for line in errors: print(line, flush=True)
raise SystemExit(0 if complete and not errors else 1)
