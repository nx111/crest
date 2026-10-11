"""Regression checks for false host-side passes; no ADB, Unity or GPU required."""
from contextlib import redirect_stdout
import io
from pathlib import Path
import runpy
import subprocess
import tempfile
import unittest
from unittest.mock import patch

from android_audit_checks import (
    QUIT_RESULT, final_result_errors, full_log_errors, minimal_result_errors, wait_for_process_exit,
)

TOOLS = Path(__file__).resolve().parent
EVIDENCE = TOOLS.parents[1] / "docs/_media/urp-realism/2026-10-11-9df1db9f"
GOOD_RESULT = ("RESULT: PASS\n" + QUIT_RESULT + "\n").encode()
STARTUP_LOG = b"10-11 22:25:37.998 4410 4410 V Unity : Context Type: ActivityOrService\n"
GOOD_LOG = STARTUP_LOG + b"10-11 22:34:35.496 4410 4514 I Unity : CREST_QUERY_QUIT_AUDIT PASS\n"
MINIMAL_LOG = STARTUP_LOG + b"10-11 22:34:35.496 4410 4514 I Unity : MINIMAL_ANDROID_CONTROL_COMPLETE\n"


def completed(output=b"", code=0):
    return subprocess.CompletedProcess([], code, output)


class ExitChecks(unittest.TestCase):
    def wait(self, responses, timeout=2):
        now = [0.0]
        replies = iter(responses)

        def adb(*args, **kwargs):
            reply = next(replies)
            if isinstance(reply, Exception):
                raise reply
            return reply

        def sleep(seconds):
            now[0] += seconds

        return wait_for_process_exit(adb, "test.app", timeout, clock=lambda: now[0], sleep=sleep)

    def test_observed_process_then_verified_exit(self):
        for code in (0, 1):
            with self.subTest(code=code):
                self.assertEqual(self.wait([completed(b"4410\n"), completed(code=code)]), (True, None))

    def test_live_process_at_deadline_is_failure(self):
        exited, error = self.wait([completed(b"4410\n")] * 3)
        self.assertFalse(exited)
        self.assertIn("did not exit", error)

    def test_adb_error_does_not_mean_process_exited(self):
        for reply in (completed(b"error: device offline", 1), completed(code=255),
                      completed(b"unexpected reply"), subprocess.TimeoutExpired("adb", 1)):
            with self.subTest(reply=reply):
                exited, error = self.wait([reply])
                self.assertFalse(exited)
                self.assertIn("could not be verified", error)


class ResultChecks(unittest.TestCase):
    def test_result_must_be_successfully_read_complete_and_unambiguous(self):
        for result in (completed(GOOD_RESULT, 1), completed(), completed(b"RESULT: PASS\nRESULT: PASS\n"),
                       completed(GOOD_RESULT + b"RESULT: FAIL\n"), completed(GOOD_RESULT + b"FAIL: late shutdown\n")):
            with self.subTest(result=result):
                self.assertTrue(final_result_errors(result, completed(GOOD_LOG), require_quit=True))

    def test_quit_requires_both_result_assertion_and_log_marker(self):
        for result, log in ((b"RESULT: PASS\n", GOOD_LOG), (GOOD_RESULT, b"I Unity : regular test PASS"),
                            (GOOD_RESULT, GOOD_LOG + b"I Unity : CREST_QUERY_QUIT_AUDIT FAIL\n")):
            with self.subTest(result=result, log=log):
                self.assertTrue(final_result_errors(completed(result), completed(log), require_quit=True))
        self.assertFalse(final_result_errors(completed(b"RESULT: PASS\n"), completed(GOOD_LOG), require_quit=False))

    def test_full_log_requires_retrieval_and_rejects_shutdown_errors(self):
        for log in (completed(GOOD_LOG, 1), completed(), completed(b"adb: disconnected"),
                    completed(GOOD_LOG.replace(STARTUP_LOG, b"")),
                    completed(GOOD_LOG + b"I Unity : GarbageCollector disposing of ComputeBuffer\n"),
                    completed(GOOD_LOG + b"I Unity : CREST_QUERY_QUIT_AUDIT FAIL\n"),
                    completed(GOOD_LOG + b"10-11 22:34:36.000 4410 4410 E AndroidRuntime : fatal exception\n")):
            with self.subTest(log=log):
                self.assertTrue(full_log_errors(log))

    def test_minimal_control_requires_complete_log(self):
        self.assertFalse(minimal_result_errors(completed(MINIMAL_LOG)))
        self.assertTrue(minimal_result_errors(completed(GOOD_LOG)))
        self.assertTrue(minimal_result_errors(completed(MINIMAL_LOG, 1)))
        for name, should_fail in (("android-empty-no-dependency-logcat.txt", True),
                                  ("android-empty-with-dependency-logcat.txt", False)):
            with self.subTest(name=name):
                self.assertEqual(bool(minimal_result_errors(completed((EVIDENCE / name).read_bytes()))), should_fail)

    def test_archived_final_android_evidence_satisfies_content_checks(self):
        result = completed((EVIDENCE / "android-final-results.txt").read_bytes())
        log = completed((EVIDENCE / "android-final-logcat.txt").read_bytes())
        self.assertFalse(final_result_errors(result, log, require_quit=True))
        self.assertFalse(full_log_errors(log))
        # Content replay does not independently establish process exit on a device.
        missing_quit = completed(log.stdout.replace(b"CREST_QUERY_QUIT_AUDIT PASS", b"QUIT_MARKER_REMOVED"))
        self.assertTrue(final_result_errors(result, missing_quit, require_quit=True))


class DriverChecks(unittest.TestCase):
    def run_driver(self, script, *, exited=True, result=None, log=None):
        """Exercise each real CLI with fake ADB replies and a controlled exit observation."""
        is_minimal = script == "run_minimal_android.py"
        final_result = result if result is not None else completed(GOOD_RESULT)
        full_log = log if log is not None else completed(MINIMAL_LOG if is_minimal else GOOD_LOG)
        cat_reads = 0

        def adb(command, **kwargs):
            nonlocal cat_reads
            arguments = command[3:]  # executable, -s, serial
            if arguments[:2] == ["shell", "cat"]:
                cat_reads += 1
                if cat_reads == 1:
                    return completed(b"not written yet", 1)
                return completed(GOOD_RESULT) if cat_reads == 2 else final_result
            if arguments[:2] == ["shell", "pidof"]:
                return completed(b"4410\n")
            if arguments[0] == "logcat":
                return full_log
            return completed()

        with tempfile.TemporaryDirectory() as temporary:
            project = Path(temporary)
            (project / "AndroidPlayer").mkdir()
            (project / "AndroidPlayer/CrestAudit.apk").write_bytes(b"fake APK for host-only test")
            argv = [str(TOOLS / script), str(project), "--adb", "fake-adb", "--device", "test-serial"]
            argv += ["--label", "test"] if is_minimal else ["--extra=-crestQuitAudit"]
            observation = (True, None) if exited else (False, "App did not exit within the deadline")
            with patch("sys.argv", argv), patch("subprocess.run", side_effect=adb), patch("time.sleep"), \
                    patch("android_audit_checks.wait_for_process_exit", return_value=observation), \
                    redirect_stdout(io.StringIO()), self.assertRaises(SystemExit) as stopped:
                runpy.run_path(str(TOOLS / script), run_name="__main__")
            return stopped.exception.code

    def test_both_drivers_accept_a_complete_successful_exit(self):
        for script in ("run_android_audit.py", "run_minimal_android.py"):
            with self.subTest(script=script):
                self.assertEqual(self.run_driver(script), 0)

    def test_both_drivers_reject_a_live_process_despite_pass_markers(self):
        for script in ("run_android_audit.py", "run_minimal_android.py"):
            with self.subTest(script=script):
                self.assertEqual(self.run_driver(script, exited=False), 1)

    def test_audit_driver_uses_final_read_and_requested_quit_probe(self):
        for result, log in ((completed(), None), (completed(GOOD_RESULT, 1), None),
                            (completed(GOOD_RESULT + b"FAIL: shutdown\n"), None),
                            (None, completed(b"I Unity : ordinary checks passed\n")),
                            (None, completed(GOOD_LOG.replace(STARTUP_LOG, b"")))):
            with self.subTest(result=result, log=log):
                self.assertEqual(self.run_driver("run_android_audit.py", result=result, log=log), 1)

    def test_minimal_driver_rejects_logcat_failure_even_with_completion_marker(self):
        self.assertEqual(self.run_driver("run_minimal_android.py", log=completed(MINIMAL_LOG, 1)), 1)


class DriverProcessIdentityChecks(unittest.TestCase):
    def run_scenario(self, scenario):
        """Run the real driver across process replacement and final-result boundaries."""
        now = [0.0]
        cat_reads = 0
        pid_reads = 0
        launches = 0
        observed = []
        final_log_pids = []
        ready_at = 3 if scenario == "periodic" else 4 if scenario == "background" else 2

        def adb(command, **kwargs):
            nonlocal cat_reads, pid_reads, launches
            arguments = command[3:]
            if arguments[:2] == ["shell", "cat"]:
                cat_reads += 1
                return completed(GOOD_RESULT) if cat_reads >= ready_at else completed(b"not ready", 1)
            if arguments[:3] == ["shell", "am", "start"]:
                launches += 1
            if arguments[:2] == ["shell", "pidof"]:
                pid_reads += 1
                reply = completed(b"4410\n")
                if scenario == "periodic" and now[0] >= 31:
                    reply = completed(b"5522\n")
                elif scenario == "background" and launches > 1:
                    reply = completed(b"5522\n")
                elif scenario == "at_result" and pid_reads >= 2:
                    reply = completed(b"5522\n")
                elif scenario == "during_wait" and pid_reads >= 3:
                    reply = completed(b"5522\n")
                elif scenario == "exit" and pid_reads >= 2:
                    reply = completed(code=1)
                elif scenario == "adb_failure" and pid_reads >= 2:
                    reply = completed(b"error: device offline", 1)
                observed.append((now[0], reply.stdout.strip()))
                return reply
            if arguments[0] == "logcat":
                if "Unity:I" in arguments:
                    return completed(b"I Unity : CREST_BACKGROUND_READY\n")
                final_log_pids.extend(value for value in arguments if value.startswith("--pid="))
                return completed(GOOD_LOG)
            return completed()

        def sleep(seconds):
            now[0] += 31 if scenario == "periodic" else 6 if scenario == "background" else seconds

        def observed_exit(observe, package, **kwargs):
            if scenario == "during_wait":
                observe("shell", "pidof", package, check=False)
            return True, None

        with tempfile.TemporaryDirectory() as temporary:
            project = Path(temporary)
            (project / "AndroidPlayer").mkdir()
            (project / "AndroidPlayer/CrestAudit.apk").write_bytes(b"fake host-test APK")
            flags = "-crestQuitAudit" + (" -crestBackgroundAudit" if scenario == "background" else "")
            argv = [str(TOOLS / "run_android_audit.py"), str(project), "--adb", "fake-adb",
                    "--device", "test-serial", "--extra=" + flags]
            with patch("sys.argv", argv), patch("subprocess.run", side_effect=adb), \
                    patch("time.monotonic", side_effect=lambda: now[0]), patch("time.sleep", side_effect=sleep), \
                    patch("android_audit_checks.wait_for_process_exit", side_effect=observed_exit), \
                    redirect_stdout(io.StringIO()), self.assertRaises(SystemExit) as stopped:
                runpy.run_path(str(TOOLS / "run_android_audit.py"), run_name="__main__")
            errors = next(project.glob("AndroidCaptures-*/acceptance-errors.txt")).read_text(encoding="utf-8")
            return stopped.exception.code, errors, final_log_pids, observed

    def test_replaced_process_cannot_hide_the_original_process_log(self):
        for scenario in ("periodic", "at_result", "background", "during_wait"):
            with self.subTest(scenario=scenario):
                code, errors, log_pids, observed = self.run_scenario(scenario)
                self.assertEqual(code, 1)
                self.assertIn("PID changed from 4410 to 5522", errors)
                self.assertEqual(log_pids, ["--pid=4410"])
                if scenario == "background":
                    self.assertIn((12.0, b"5522"), observed)  # Checked at resume, before the next result poll.

    def test_absent_pid_at_final_result_is_a_normal_exit(self):
        code, errors, log_pids, _ = self.run_scenario("exit")
        self.assertEqual(code, 0)
        self.assertEqual(errors, "")
        self.assertEqual(log_pids, ["--pid=4410"])

    def test_pid_observation_failure_cannot_become_a_pass(self):
        code, errors, log_pids, _ = self.run_scenario("adb_failure")
        self.assertEqual(code, 1)
        self.assertIn("PID could not be verified", errors)
        self.assertEqual(log_pids, ["--pid=4410"])


if __name__ == "__main__":
    unittest.main()
