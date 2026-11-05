# macOS migration ownership — contract B0

## Active WPF parity cleanroom phase — 2026-11-05

Current primary checkout uses `features/wpf-parity`, inheriting the validated migration checkpoint
`3d9205cc`. The two isolated worker branches are `features/wpf-parity-shell` and
`features/wpf-parity-settings`; old source branches and dirty worktrees remain preserved.
The user explicitly requested reference-faithful Avalonia UI migration and parallel branch work.
Only screenshots and sanitized observable notes from `UI-Reference-WPF-20261105.zip` are used
for UI reconstruction. Do not read legacy WPF UI/XAML or the archive's XAML-derived control
structures/source mappings/collector scripts. Existing portable business services remain reusable.
The exact per-phase write contract is `.shou-dev/reports/wpf-parity/CONTRACT.md`.
Main owns Core integration and instance behavior; shell owns shared chrome/home/tools/download
views and VMs; settings owns setting/account views and the separate new World/Server NBT backend.
Two workers only, gpt-6.1-sol/max, no recursive agents. All heavy jobs use the shared lock and budget.
Local phased commits in these three branches only; no push, PR merge, release, history rewrite,
original .env modification or legacy deletion before parity/native/data gates pass. Reference
coverage, visual matching and working business behavior must be reported separately.

## Previous native validation checkpoint — 2026-11-05

Current development uses `features/macos-native-validation` in the primary repository checkout.
The previous integration worktree is retained detached at the validation checkpoint.
The user explicitly authorized combining the three local feature histories and continuing here.
Source heads were `features/macos-arm64` at `7decdd17`, UI at `8e5fc6f8`, and platform at `de6ebe86`.
All worker-owned implementation paths matched the reviewed integration tree before merging.
Two history-reconciliation merges retain that exact tree and make all three source heads ancestors;
the older shared scaffolding and lock files do not replace subsequent tested fixes.
The old branches and their uncommitted shared inputs remain preserved. New fixes and validation
notes belong on the new branch. Push, PR merge, tags, remote Actions and publication remain
unauthorized. The overnight cutoff and its no-merge rule are historical, superseded only for
the explicitly requested local consolidation. Evidence lives in `.shou-dev/reports/day-native/`.

## Historical overnight ownership

Base HEAD for all worktrees: `b97b565678cbcdc8f602b57973d0837d3ac556a2`.
Local phase commits on the three named feature branches are authorized (reports/night/local-commit-authorization.json). Use existing identity/signing/hooks, Angular messages via -F, and validated stage content. Push, PR, merge, cherry-pick, rebase, reset, clean, tag, amend, Actions dispatch and release remain unauthorized.

| Owner | Exclusive writes |
|---|---|
| main | solutions, all csproj/global.json/locks; shared interfaces/DTO; non-Java Core; Program/Bootstrap; generators; build/CI/package scripts; migration docs and PROGRESS.json |
| ui_worker | Desktop/Views, ViewModels, Themes, Assets, App.axaml and App.axaml.cs; Desktop.Tests test content |
| platform_worker | Desktop/Platform (Windows, MacOS, required shared implementation); Core/Java; Core.Tests/Platform and Java test content |

Workers write only their absolute worktree. No recursive agents. All three use configured gpt-6.1-sol/max.
Before a handoff, freeze changed files and produce an exact patch (including new files) against received B0/Bn, SHA-256 manifest and structured handoff under the role's reports directory. Main reviews and applies sequentially; never copy a whole directory over another owner.
Every heavy operation uses `.shou-dev/control/dev.py`, one machine-wide lock, independent job artifacts. No credentials in argv, reports or generated source dumps. No HOME/CODEX_HOME/system changes.
Main seeded App for B0 compilation; ownership transfers to UI after B0 distribution. Core API additions are requested from main.


B8 scope: main writes JavaTrustContracts/shared settings and manifest DTOs, dependency pins/locks, LauncherService.JavaTrust/automatic-selection orchestration, CLI and reports. UI owns Settings scan/copy display and protocol Headless tests. Platform owns the migrated trust policy/JKS/PKCS12 engine, independent runtime trees/provenance, discovery pre-execution checks and real-copy/keytool tests. Shared DTO/csproj/lock changes supplied by main stay outside worker commits and own-only patches, with dirty prerequisite hashes recorded. Original tool/B5 JDKs are read-only; only fresh derived copies may be repaired. Native Windows/GUI/external-account acceptance stays separate from fixtures and cross-publish.

Final N5 candidate source is clean integration commit `573ce8e74b9abf429aa6b882b055ebc16c123832`; UI own HEAD is `8e5fc6f87d2110a372d3baf0885098b28c3135a2`, platform own HEAD is `de6ebe869e7fd1b3fb786bb3268ee65901b17068`. B8/B8.1/B9 and the late credential own-only increments were reviewed and sequentially applied; shared prerequisite dirty files remain explicitly excluded from worker commits. Main personally owns the account persistence/rotated-session fix; platform owns the process-local native Keychain guard and its three new native tests. Final role handoffs live under `reports/night/handoffs/<role>/FINAL-handoff.json`; main owns the final delivery index, precise source patches and truthful incomplete-gate report. Documentation-only commits after the frozen source do not change the candidates' BUILD-INFO provenance.
