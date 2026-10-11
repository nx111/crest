"""Host-side acceptance checks shared by the disposable Android diagnostics."""
import re
import subprocess
import time


QUIT_RESULT = "PASS: Immediate shutdown releases live and already-cleaned providers before native teardown"
QUIT_PASS = re.compile(r"\bCREST_QUERY_QUIT_AUDIT PASS\b")
QUIT_FAIL = re.compile(r"\bCREST_QUERY_QUIT_AUDIT FAIL\b")


def wait_for_process_exit(adb, package, timeout, interval=1, *, clock=time.monotonic, sleep=time.sleep):
    """Return (exited, error); an ADB failure must never count as an absent PID."""
    deadline = clock() + timeout
    while True:
        try:
            result = adb("shell", "pidof", package, check=False,
                         timeout=min(30, max(1, deadline - clock())))
        except (OSError, TimeoutError, subprocess.TimeoutExpired) as error:
            return False, "App exit could not be verified: " + str(error)
        output = result.stdout.decode("utf-8", errors="replace").strip()
        # Android pidof normally returns 1 for no matches; some versions return 0.
        if result.returncode in (0, 1) and not output:
            return True, None
        if result.returncode != 0 or not all(pid.isdigit() for pid in output.split()):
            return False, f"App exit could not be verified: pidof returned {result.returncode}: {output}"
        remaining = deadline - clock()
        if remaining <= 0:
            return False, f"App did not exit within {timeout:g} seconds; PID(s): {output}"
        sleep(min(interval, remaining))


def full_log_errors(logcat):
    """Inspect the app's entire PID-filtered Unity/native log, including shutdown."""
    lines = logcat.stdout.decode("utf-8", errors="replace").splitlines()
    errors = [line for line in lines
              if " E Unity" in line or " E AndroidRuntime" in line or "Fatal signal" in line
              or "GarbageCollector disposing" in line or "Unreleased non-evictable" in line
              or "Leak Detected" in line or "JobTempAlloc" in line or QUIT_FAIL.search(line)]
    if logcat.returncode or not any(" Unity" in line for line in lines):
        errors.append("Full Unity startup/runtime log could not be retrieved for the app PID")
    # This pinned Unity Android player logs its context before the AssetPackManager
    # probe. Engine initialization happens later and cannot prove the error was retained.
    if not any(" Unity" in line and "Context Type: ActivityOrService" in line for line in lines):
        errors.append("Early Unity startup log is missing; the retained logcat may be truncated")
    return errors


def final_result_errors(result, logcat, *, require_quit):
    """Validate the file read after process exit, including a requested quit probe."""
    errors = []
    lines = [line.strip() for line in result.stdout.decode("utf-8", errors="replace").splitlines()]
    if result.returncode:
        errors.append(f"Final results.txt could not be retrieved (exit {result.returncode})")
    if [line for line in lines if line.startswith("RESULT:")] != ["RESULT: PASS"]:
        errors.append("Final results.txt must contain exactly one RESULT: PASS and no other RESULT marker")
    if any("FAIL:" in line for line in lines):
        errors.append("Final results.txt contains a failed assertion")
    if require_quit:
        if lines.count(QUIT_RESULT) != 1:
            errors.append("Final results.txt is missing the successful native-query shutdown assertion")
        log_text = logcat.stdout.decode("utf-8", errors="replace")
        if not QUIT_PASS.search(log_text) or QUIT_FAIL.search(log_text):
            errors.append("Full process log must contain CREST_QUERY_QUIT_AUDIT PASS without a shutdown failure")
    return errors


def minimal_result_errors(logcat):
    errors = full_log_errors(logcat)
    if b"MINIMAL_ANDROID_CONTROL_COMPLETE" not in logcat.stdout:
        errors.append("Empty control did not report MINIMAL_ANDROID_CONTROL_COMPLETE")
    return errors
