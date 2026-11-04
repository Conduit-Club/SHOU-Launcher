# macOS migration ownership — contract B0

Base HEAD for all worktrees: `b97b565678cbcdc8f602b57973d0837d3ac556a2`.
Changes are uncommitted. No push, PR, merge, tag, Actions dispatch or release is authorized.

| Owner | Exclusive writes |
|---|---|
| main | solutions, all csproj/global.json/locks; shared interfaces/DTO; non-Java Core; Program/Bootstrap; generators; build/CI/package scripts; migration docs and PROGRESS.json |
| ui_worker | Desktop/Views, ViewModels, Themes, Assets, App.axaml and App.axaml.cs; Desktop.Tests test content |
| platform_worker | Desktop/Platform (Windows, MacOS, required shared implementation); Core/Java; Core.Tests/Platform and Java test content |

Workers write only their absolute worktree. No recursive agents. All three use configured gpt-6.1-sol/max.
Before a handoff, freeze changed files and produce an exact patch (including new files) against received B0/Bn, SHA-256 manifest and structured handoff under the role's reports directory. Main reviews and applies sequentially; never copy a whole directory over another owner.
Every heavy operation uses `.shou-dev/control/dev.py`, one machine-wide lock, independent job artifacts. No credentials in argv, reports or generated source dumps. No HOME/CODEX_HOME/system changes.
Main seeded App for B0 compilation; ownership transfers to UI after B0 distribution. Core API additions are requested from main.
