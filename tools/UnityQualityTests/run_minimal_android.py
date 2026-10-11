"""Collect the empty Unity Android control's complete, own-process startup/exit log."""
import argparse
from pathlib import Path
import subprocess

from android_audit_checks import minimal_result_errors, wait_for_process_exit

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
    return result

print(adb("install", "-r", str(args.project / "MinimalPlayer/Empty.apk"), timeout=180).stdout.decode(), flush=True)
launch = adb("shell", "am", "start", "-W", "-n", package + "/com.unity3d.player.UnityPlayerActivity", timeout=60)
(output / "launch.txt").write_bytes(launch.stdout)
print(launch.stdout.decode(), flush=True)
pid = adb("shell", "pidof", package).stdout.decode().strip()
if not pid.isdigit():
    raise RuntimeError("Empty control process PID was not observed")
exited, exit_error = wait_for_process_exit(adb, package, timeout=100, interval=2)
log = adb("logcat", "-d", "--pid=" + pid, "-s", "Unity:*", "AndroidRuntime:*", "libc:*", check=False)
(output / "logcat.txt").write_bytes(log.stdout)
errors = minimal_result_errors(log)
if exit_error:
    errors.append(exit_error)
complete = b"MINIMAL_ANDROID_CONTROL_COMPLETE" in log.stdout
summary = ("PID=" + pid + "; controlCompleted=" + str(complete)
           + "; processExited=" + str(exited) + "; acceptanceErrors=" + str(len(errors)))
(output / "results.txt").write_text(summary + "\n" + "\n".join(errors), encoding="utf-8")
print(summary, flush=True)
for line in errors: print(line, flush=True)
raise SystemExit(0 if not errors else 1)
