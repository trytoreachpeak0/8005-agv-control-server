"""Reverse verification for control-server#340: one injected failure at a time, each restored by git checkout.

Run from the control-server worktree with a clean tree. Every replacement must match exactly once, or the run stops
before any test: an injection that did not apply looks exactly like a green.
"""
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path.cwd()
OUT = Path(sys.argv[1])
OUT.mkdir(parents=True, exist_ok=True)
PROC = "src/ControlServer.Host/Transport/OnboardMessageProcessor.cs"
STORE = "src/ControlServer.Infrastructure/Persistence/WireToGateStore.cs"

TESTS = [
    "ASafetyChangeFirstDeliveredInTheReconnectHandshakeIsOnlyAcknowledged",
    "AnOperationResultInTheReconnectHandshakeIsOnlyAcknowledgedEvenWhenItChangesReadiness",
    "ARecoveryResultFirstDeliveredInTheReconnectHandshakeIsOnlyAcknowledgedEvenWhenItChangesReadiness",
    "AMessageArrivingASecondTimeInTheReconnectHandshakeIsOnlyAcknowledgedEvenWhenItChangesReadiness",
    "AHardwareRecoveryRecordInsideTheReconnectHandshakeIsAnsweredWithoutReadinessEvenWhenItChangesReadiness",
    "InsideTheReconnectHandshakeReadinessCannotChangeBeforeTheRecoveryReport",
    "TheRecoveryReportAnnouncesTheReadinessAResendInsideTheHandshakeLeftBehind",
    "AfterTheReconnectHandshakeTheSameMessagesStillCarryReadiness",
    "AVehicleNotReadyOnItsOwnOrderHandshakesWithoutAnExtraReadinessAndIsStillToldAfterwards",
    "OnboardHandshakeReadinessArchitectureTests",
]

def inline(answer, decision, on_change=True, indent=" " * 20):
    # The site as it was before #340, statement for statement: append on a change of readiness (or always, for a
    # safety change), handshake or not.
    backslash_n = chr(92) + "n"
    line = f'return $"{{{answer}}}{backslash_n}{{SessionReadinessLine({decision}, agvId, generation, state)}}";'
    body = []
    if on_change:
        body += [f"if ({decision}.Readiness == state.Readiness)", "{", f"    return {answer};", "}"]
    body += [f"state.Readiness = {decision}.Readiness;", line]
    return ("\n" + indent).join(body)


MUTATIONS = {
    "M0-combinator-ignores-handshake": [(PROC,
        "if (!state.HandshakeCompleted || !(changed || announceUnchanged))",
        "if (!(changed || announceUnchanged))")],
    "M1-safety-change-inline": [(PROC,
        "return AnswerWithReadiness(ack, decision, agvId, generation, state, announceUnchanged: true);",
        inline("ack", "decision", on_change=False))],
    "M2-operation-result-inline": [(PROC,
        "return AnswerWithReadiness(\n                        resultAck, resultDecision, agvId, generation, state, announceUnchanged: false);",
        inline("resultAck", "resultDecision"))],
    "M3-recovery-result-inline": [(PROC,
        "return AnswerWithReadiness(\n                        recoveryAck, recoveryDecision, agvId, generation, state, announceUnchanged: false);",
        inline("recoveryAck", "recoveryDecision"))],
    "M4-duplicate-arrival-inline": [(PROC,
        "return AnswerWithReadiness(ack, decision, agvId, generation, state, announceUnchanged: false);",
        inline("ack", "decision", indent=" " * 8))],
    "M5-hardware-record-inline": [(PROC,
        "return AnswerWithReadiness(\n                        recordResult, recordDecision, agvId, generation, state, announceUnchanged: false);",
        inline("recordResult", "recordDecision"))],
    "M6-combinator-never-appends": [(PROC,
        "if (!state.HandshakeCompleted || !(changed || announceUnchanged))",
        "if (Environment.TickCount64 >= 0)")],
    "G1-sixth-site-appends-readiness": [(PROC,
        "            case \"OperationProgress\":\n            case \"PreDepartureSafetyCheckResult\":\n            case \"SublotSubmitted\":\n                return DurableAck(messageType, messageId, agvId, generation, contentHash);",
        "            case \"OperationProgress\":\n            case \"PreDepartureSafetyCheckResult\":\n            case \"SublotSubmitted\":\n                {\n                    SessionReadinessDecision progressDecision = await store.DecideReadinessAsync(\n                        agvId, generation, cancellationToken).ConfigureAwait(false);\n                    return $\"{DurableAck(messageType, messageId, agvId, generation, contentHash)}\\n{SessionReadinessLine(progressDecision, agvId, generation, state)}\";\n                }")],
    "P1-ready-without-recovery-report": [(STORE,
        "row.RecoveryReportId is not null && departureUsable && noPendingFacts &&",
        "departureUsable && noPendingFacts &&")],
}


def run(cmd, log):
    result = subprocess.run(cmd, cwd=ROOT, capture_output=True, text=True, encoding="utf-8", errors="replace")
    log.write_text(result.stdout + result.stderr, encoding="utf-8", newline="")
    return result


def main():
    if subprocess.run(["git", "status", "--porcelain"], cwd=ROOT, capture_output=True, text=True).stdout.strip():
        sys.exit("worktree not clean")
    head = subprocess.run(["git", "rev-parse", "HEAD"], cwd=ROOT, capture_output=True, text=True).stdout.strip()
    only = sys.argv[2:] or list(MUTATIONS)
    summary = [f"HEAD {head}"]
    test_filter = "|".join(f"FullyQualifiedName~{name}" for name in TESTS)
    for name in only:
        touched = set()
        try:
            for file, old, new in MUTATIONS[name]:
                path = ROOT / file
                text = path.read_text(encoding="utf-8")
                count = text.count(old)
                if count != 1:
                    sys.exit(f"{name}: expected 1 match in {file}, found {count}")
                path.write_text(text.replace(old, new), encoding="utf-8", newline="")
                touched.add(file)
            diff = subprocess.run(["git", "diff", "--numstat"], cwd=ROOT, capture_output=True, text=True).stdout.strip()
            (OUT / f"{name}.diff").write_text(
                subprocess.run(["git", "diff"], cwd=ROOT, capture_output=True, text=True).stdout, encoding="utf-8", newline="")
            build = run(["dotnet", "build", "tests/ControlServer.Tests/ControlServer.Tests.csproj", "-c", "Release",
                         "--no-incremental"], OUT / f"{name}.build.txt")
            if build.returncode != 0 or not re.search(r"\b0 Error\(s\)", build.stdout):
                summary.append(f"{name}: BUILD FAILED ({diff})")
                continue
            test = run(["dotnet", "test", "tests/ControlServer.Tests/ControlServer.Tests.csproj", "-c", "Release",
                        "--no-build", "--filter", test_filter, "--logger", "console;verbosity=normal"],
                       OUT / f"{name}.test.txt")
            failed = sorted(set(re.findall(r"^\s+Failed (ControlServer\.Tests\.\S+?)(?:\(|\s\[)", test.stdout, re.M)))
            total = re.findall(r"(Failed!|Passed!)\s+- Failed:\s+(\d+), Passed:\s+(\d+)", test.stdout)
            summary.append(f"{name}: diff {diff!r}; {total}; red: {', '.join(failed) or '(none)'}")
        finally:
            for file in touched:
                subprocess.run(["git", "checkout", "--", file], cwd=ROOT, check=True)
    (OUT / "SUMMARY.txt").write_text("\n".join(summary) + "\n", encoding="utf-8", newline="")
    print("\n".join(summary))


main()
