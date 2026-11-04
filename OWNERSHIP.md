# macOS migration ownership — contract B0

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

Final candidate source is clean integration commit `5f99195b95b26cb6c96af8ca67c67775ca44a6ea`; UI own HEAD is `8e5fc6f87d2110a372d3baf0885098b28c3135a2`, platform own HEAD is `9e65168fe895d1f40d3e0625240782be729876f0`. B8/B8.1/B9 own-only increments were reviewed and sequentially applied; shared prerequisite dirty files remain explicitly excluded from worker commits. Final role handoffs live under `reports/night/handoffs/<role>/FINAL-handoff.json`; main owns the final delivery index, precise source patches and truthful incomplete-gate report. Documentation-only commits after the frozen source do not change the candidates' BUILD-INFO provenance.
