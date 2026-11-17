# Framework follow-up review

2026-11-16. Reviewed the current changes in native IPC, `elevation.h`, `package.h`, `exec.h`, and Runner Quick Access/settings/tray/main/update/AI Hub paths. Source findings and the final coordinated build/test evidence are recorded separately below.

Three concrete transport issues were corrected during the follow-up:

1. The new 1 Mi-character input cap counted allocated 512-character blocks instead of actual `bytesRead`. An otherwise valid maximum payload with a terminating NUL was rejected; odd byte counts were silently accepted as padded UTF-16. The reader now appends actual complete code units, permits one optional wire terminator, checks the payload length, and drops empty payloads before the queue's empty-string shutdown sentinel.
2. The dedicated input consumer was a raw `std::thread` without a COM apartment. Quick Access's new whitelist and the existing AI Hub response callback use WinRT JSON there. The consumer now initializes MTA and pairs uninitialization with thread exit. `start()` waits for that initialization and propagates its exception synchronously, so an unusable consumer cannot be reported as started.
3. The new 16-handler admission limit ran before finished handlers were reaped. If 16 idle clients all timed out while the listener waited, the first healthy follow-up client was refused using stale occupancy. Finished handlers are now reaped before admitting the new connection.

Added `src/common/interop/native-tests/Common.Interop.NativeTests.vcxproj`, initially with these five IPC behavior tests; the final project includes nine IPC and three Quick Access lifecycle tests:

- Duplicate pipe ownership fails synchronously; injected failure after both queue threads start rolls them back and permits repeated shutdown.
- Both forms of the exact maximum payload are received intact; an oversized message, incomplete UTF-16 code unit and empty payload are rejected, followed by successful dispatch.
- A throwing callback does not kill the consumer; its following callback observes MTA and parses WinRT JSON.
- All 16 idle connections time out and disconnect; the first subsequent healthy client is dispatched.
- An exact caller PID mismatch is rejected before dispatch; the actual testhost PID, directory and basename are accepted, including a second connection through the process-verification cache. Signature verification is explicitly disabled and expected version is zero for the local test binary.

The test project compiles the production transport and authentication sources directly, uses existing CppWinRT/WIL packages, and does not reference removed Workspaces modules. The new LightSwitch CLI test project also now declares `RuntimeIdentifier=win-x64` and `OutputType=Exe`, resolving the reported SelfContained/RID project configuration error.

No further concrete regression was found in the inspected elevation ownership/creation flags, package enumeration/version/removal result, unique subprocess-pipe naming, authenticated suspended Quick Access startup, update badge UI dispatch, or AI Hub UI-thread guard changes. That is not runtime proof. Existing broader concerns such as unbounded queue storage, allocator failures escaping worker entry points, and calling transport shutdown from its own callback were not introduced by this change and remain outside these focused corrections.

Static validation included project/package XML parsing and scoped `git diff --check`. Build and runtime verification were executed by the parent task, with the final observed results below.

## Quick Access lifecycle follow-up

K7 also applied to disabling Quick Access: the settings dispatcher called `stop()` on the Runner UI thread and waited up to 2 + 5 seconds. Quick Access now owns a single joined worker, with startup/show/stop intent serialized there. `request_stop()` returns after submitting the latest intent; final `stop(wait_for_process)` closes the queue and joins, with a cancellation event to skip grace periods during system session shutdown. No callback or process wait runs under the request mutex, except the bounded show-event signal. State queries avoid blocking on process shutdown. If ordinary shutdown cannot finish, process ownership is preserved so a later show cannot launch a duplicate child.

Three behavior tests exercise the production worker: stop submission during blocked startup suppresses stale show; show submitted during blocked stop runs afterward on the same worker; final fast shutdown cancels the stop wait, discards pending show, and rejects later requests. Parent owns the general-settings `request_stop()` and final Runner `stop(!is_system_session_ending())` integration.

## Native pipe burst follow-up

The first coordinated smoke run observed `Sent=1000 Received=602 Reordered=0 Corrupted=0` in `TestResults/stable-review-pipe-smoke-2.log`. Source inspection identified accepting then refusing connections at the new 16-handler cap, plus the short-write/early-close connection race described below; the run did not separately quantify each loss path. The listener now waits for capacity before accepting. Each accepted handler retains its result in the existing slot; only completed leading slots publish to the input queue, so slow/failed earlier connections cannot reorder later messages. Rejected, timed-out and exception paths complete their slots. These ordering slots remain bounded by the same 16-handler capacity.

The sender retries a missing listener for up to five seconds without advancing the queue. A busy listener applies backpressure in short waits until shutdown. After writing, the sender uses a cancelable overlapped read to wait for the server's normal per-message disconnect, with a ten-second ceiling. No acknowledgement bytes or retransmission are added. This keeps a short buffered write alive until `ConnectNamedPipe` and the read finish; the server closes the pipe in handler completion, before thread reaping. `end()` cancels the active write/read and wakes the listener's capacity wait.

Four additional native tests cover a 1000-message burst behind 16 occupied handlers, delayed listener creation, deliberately reversed read-completion order plus a malformed preceding slot, and canceling a sender whose peer deliberately keeps its pipe open. The existing standalone smoke assertions are unchanged.

## Verified coordinated results

The final Debug x64 solution build succeeded with zero warnings and zero errors (`build.debug.x64.all.log`). `TestResults/Stable3101Final/status.json` records exit code zero for NativeInterop, PipeSmoke, LightSwitchService and Settings. The corresponding logs and TRX files confirm:

| Check | Observed result | Evidence |
|---|---|---|
| Native IPC and Quick Access lifecycle | 12/12 passed, including capacity, ordering, delayed startup, PID rejection and cancellation | `TestResults/Stable3101Final/NativeInterop.log`, `NativeInterop/NativeInterop.trx` |
| Ordinary pipe smoke | Sent=1000, Received=1000, Reordered=0, Corrupted=0 | `TestResults/Stable3101Final/pipe.log` |
| Delayed-listener pipe smoke | Sent=1000, Received=1000, Reordered=0, Corrupted=0 | `TestResults/Stable3101Final/pipe.log` |
| LightSwitch service tests | 147/147 passed | `TestResults/Stable3101Final/LightSwitchService.log` |
| Settings tests, including updated shortcut contracts | 311/311 passed, zero skipped | `TestResults/Stable3101Final/Settings/settings-tests.stdout.log` and its TRX |

The 16-handler and ordering-slot bound does **not** limit the total input/output message queues, which remain unbounded. The transport does not guarantee durable delivery through a crashed peer or provide an application-level acknowledgement; missing listeners have a five-second retry budget, and the post-write disconnect wait is capped at ten seconds. Tests cover the named paths, not every teardown/allocator failure or a callback invoking `end()` on its own consumer thread.
