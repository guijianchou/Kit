# AI Hub 3.4 workflow

AI Hub retains the cached WinUI 3 page and the shell Mica backdrop. Task navigation stays visible while content scrolls; each tab remembers its scroll position. Opening the page uses cached AI readiness, without starting a model probe.

## Optimization

1. Scan Downloads, known caches, or both. Local scope and age checks determine the candidates; AI reviews those candidates without authorizing additional paths.
2. Search by file name, path, category or reason. Filter by approval, skipped, attention needed or selected state. Select/deselect visible affects only matching approved candidates. Hidden selections are retained and counted.
3. Every execution button opens the same confirmation preview. Category/project buttons operate on selected approved items in that scope. The preview shows counts and bytes for the full plan, with up to 100 file details. Cache deletion is permanent. Downloads are moved only within the Downloads root.
4. The execution service captures the plan before yielding, checks each file again, and observes cancellation between actions and during Git checks. Development-cache ignore checks run in bounded batches. New, changed, linked, locked or ineligible files are not silently substituted.
5. Review the result and copy a JSON report if needed. Successful items leave the current candidates; failed items retain their reason. Cancellation preserves completed outcomes and does not replay the remaining items.

Local history is stored at `%LOCALAPPDATA%\Kit\AIHub\State\optimization_history.json`. It retains at most 20 runs, at most 500 item details per run, and at most 16 MiB. Totals remain accurate when details are truncated. Source/destination paths stay on this device unless the user copies the report. Deleted size is the sum of successfully deleted snapshot file sizes, not a measurement of physical disk space recovered. History is evidence, not an undo mechanism. An interrupted run requires a new scan before another attempt.

NuGet payload deletion requires its `.nupkg.metadata` marker to be explicitly selected and successfully handled first; execution does not delete an unselected marker as a hidden side effect.

## Task policies

The global `security.md` and `security-audit`, `system-optimization`, and `self-test` `AGENTS.md` templates are version 2. Upgrade migration matches only known shipped contents after newline/BOM normalization and preserves custom policies. Policies remain editable while the AI service is disabled.

Each policy keeps a separate draft. Switching tabs and reloading settings preserve edits. Save applies only the selected draft. Restore default loads a draft; Save is still required. Discard restores the saved content. The self-test uses the actual `findings`/`actions` contract and validates the one expected ping finding, rather than treating arbitrary findings as success.

## Validation

Core tests use temporary fixtures for execution, cancellation, file changes, move collisions, history limits, policy migration and self-test contracts. The AI Hub page smoke uses synthetic results in the real WinUI runtime and restores the original settings. It must not call the user's configured model or clean user files. Run builds serially through `tools/build/build.ps1`; run the built test executable and `tools/tests/SettingsUI/Invoke-AiHubUxSmoke.ps1` after a successful build.
