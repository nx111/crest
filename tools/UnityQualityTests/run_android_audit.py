"""Install and run the disposable Crest Vulkan audit; retrieve this app's artifacts."""
import argparse
from pathlib import Path
import shlex
import subprocess
import time

from android_audit_checks import final_result_errors, full_log_errors, wait_for_process_exit

PACKAGE = "com.crestwater.runtimeaudit"
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("project", type=Path)
parser.add_argument("--adb", type=Path, required=True)
parser.add_argument("--device", required=True)
parser.add_argument("--timeout", type=float, default=600)
parser.add_argument("--extra", default="", help="additional diagnostic Unity flags")
args = parser.parse_args()
extra_flags = shlex.split(args.extra)
project = args.project.resolve()
apk = project / "AndroidPlayer" / "CrestAudit.apk"
if not apk.is_file():
    parser.error("Build AndroidPlayer/CrestAudit.apk in a disposable project first")
output = project / ("AndroidCaptures-vulkan-" + time.strftime("%Y%m%d-%H%M%S"))
output.mkdir()
remote = f"/sdcard/Android/data/{PACKAGE}/files/{output.name}"
remote_log = f"/sdcard/Android/data/{PACKAGE}/files/{output.name}.log"
audit_pid = None
process_errors = []


def adb(*command, timeout=30, check=True):
    result = subprocess.run([str(args.adb), "-s", args.device, *command],
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=timeout)
    if result.returncode and ":" in args.device and any(
            marker in result.stdout.lower() for marker in (b"offline", b"not found", b"no devices")):
        subprocess.run([str(args.adb), "connect", args.device], stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=30)
        result = subprocess.run([str(args.adb), "-s", args.device, *command],
                                stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=timeout)
    if check and result.returncode:
        raise RuntimeError(result.stdout.decode("utf-8", errors="replace"))
    return result


def observe_process(*command, **kwargs):
    """Keep this launch's first PID, including observations made while waiting for exit."""
    global audit_pid
    try:
        result = adb(*command, **kwargs)
    except (OSError, TimeoutError, subprocess.TimeoutExpired) as error:
        result = subprocess.CompletedProcess(command, 255, str(error).encode("utf-8", errors="replace"))
    pid = result.stdout.decode("utf-8", errors="replace").strip()
    if result.returncode in (0, 1) and not pid:
        return result  # A normal absent PID remains valid during shutdown.
    if result.returncode != 0 or not pid.isdigit():
        error = f"Audit app PID could not be verified: pidof returned {result.returncode}: {pid}"
    elif audit_pid is None:
        audit_pid = pid
        return result
    elif pid != audit_pid:
        error = f"Audit app PID changed from {audit_pid} to {pid}; retaining the original process log"
    else:
        return result
    if error not in process_errors:
        process_errors.append(error)
        print("ANDROID_PROCESS_ERROR: " + error, flush=True)
    return result


print("APK_INSTALL:", adb("install", "-r", str(apk), timeout=600).stdout.decode(errors="replace"), flush=True)
command_line = f"-crestRuntimeSmoke -crestGpuContracts {args.extra} -force-vulkan -crestCapturePath {remote} -logFile {remote_log}"
launch = adb("shell", "am", "start", "-W", "-n", f"{PACKAGE}/com.unity3d.player.UnityPlayerActivity",
             "--es", "unity", shlex.quote(command_line), timeout=60)
print("APK_LAUNCH:", launch.stdout.decode(errors="replace"), flush=True)
started = time.monotonic()
next_update = started
screenshot_saved = False
background_started = None
background_resumed = False
while time.monotonic() - started < args.timeout:
    result = adb("shell", "cat", remote + "/results.txt", check=False)
    if result.returncode == 0 and (b"RESULT: PASS" in result.stdout or b"RESULT: FAIL" in result.stdout):
        # Include quitting handlers and native teardown; a managed PASS can precede buffer leaks.
        observe_process("shell", "pidof", PACKAGE, check=False)
        exited, exit_error = wait_for_process_exit(observe_process, PACKAGE, timeout=30)
        audit_errors = list(process_errors)
        if exit_error:
            audit_errors.append(exit_error)
        result = adb("shell", "cat", remote + "/results.txt", check=False)
        artifacts = adb("pull", remote + "/.", str(output), timeout=120, check=False)
        if artifacts.returncode:
            audit_errors.append("Audit artifacts could not be retrieved: " + artifacts.stdout.decode(errors="replace"))
        adb("pull", remote_log, str(output / "player-vulkan.log"), timeout=120, check=False)
        startup_errors = []
        logcat = subprocess.CompletedProcess([], 1, b"")
        if audit_pid:
            logcat = adb("logcat", "-d", "--pid=" + audit_pid, "-s", "Unity:*", "AndroidRuntime:*", "libc:*", check=False)
            (output / "player-vulkan-logcat.txt").write_bytes(logcat.stdout)
            startup_errors = full_log_errors(logcat)
        else:
            startup_errors.append("Audit app PID was not observed; full startup/runtime log is unavailable")
        audit_errors.extend(final_result_errors(result, logcat, require_quit="-crestQuitAudit" in extra_flags))
        for line in startup_errors:
            print("ANDROID_FULL_LOG_ERROR: " + line, flush=True)
        for line in audit_errors:
            print("ANDROID_AUDIT_ERROR: " + line, flush=True)
        (output / "full-log-errors.txt").write_text("\n".join(startup_errors), encoding="utf-8")
        (output / "acceptance-errors.txt").write_text("\n".join(startup_errors + audit_errors), encoding="utf-8")
        (output / "results.txt").write_bytes(result.stdout)
        print("ANDROID_ARTIFACTS=" + str(output), flush=True)
        print("ANDROID_PROCESS_EXITED=" + str(exited), flush=True)
        print(result.stdout.decode("utf-8", errors="replace"), flush=True)
        print("ANDROID_FULL_LOG_ERROR_COUNT=" + str(len(startup_errors)), flush=True)
        print("ANDROID_ACCEPTANCE_ERROR_COUNT=" + str(len(startup_errors + audit_errors)), flush=True)
        raise SystemExit(1 if startup_errors or audit_errors else 0)
    now = time.monotonic()
    if "-crestBackgroundAudit" in extra_flags and audit_pid and not background_resumed:
        if background_started is None:
            own_log = adb("logcat", "-d", "--pid=" + audit_pid, "-s", "Unity:I", check=False).stdout
            if b"CREST_BACKGROUND_READY" in own_log:
                adb("shell", "input", "keyevent", "KEYCODE_HOME")
                background_started = now
                print("ANDROID_BACKGROUND: sent Home during the audit's own background test", flush=True)
        elif now - background_started >= 5:
            adb("shell", "am", "start", "-W", "-n", f"{PACKAGE}/com.unity3d.player.UnityPlayerActivity",
                "--es", "unity", shlex.quote(command_line), timeout=60)
            observe_process("shell", "pidof", PACKAGE, check=False)
            background_resumed = True
            print("ANDROID_BACKGROUND: requested resume after five seconds; original PID=" + audit_pid, flush=True)
    if now >= next_update:
        pid = observe_process("shell", "pidof", PACKAGE, check=False).stdout.decode(errors="replace").strip()
        print(f"ANDROID_RUNNING elapsed={now-started:.0f}s; pid={pid or 'not found'}", flush=True)
        next_update = now + 30
        if not screenshot_saved:
            foreground = adb("shell", "dumpsys", "activity", "activities", check=False).stdout.decode(errors="replace")
            policy = adb("shell", "dumpsys", "window", "policy", check=False).stdout.decode(errors="replace")
            unlocked = not any(line.strip() == "showing=true" for line in policy.splitlines())
            if unlocked and any(PACKAGE in line and "mResumedActivity" in line for line in foreground.splitlines()):
                shot = adb("exec-out", "screencap", "-p", timeout=30, check=False)
                if shot.stdout.startswith(b"\x89PNG"):
                    (output / "device-screen.png").write_bytes(shot.stdout)
                    screenshot_saved = True
    time.sleep(2)
adb("pull", remote_log, str(output / "player-vulkan.log"), timeout=120, check=False)
print("ANDROID_TIMEOUT; artifacts=" + str(output), flush=True)
raise SystemExit(1)
