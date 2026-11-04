# Shared contracts — B0

Authoritative definitions: `src/SHOU.Launcher.Core/Abstractions/`. Core is net10.0 and has no old WPF/Core reference. One Desktop supports osx-arm64, win-x64 and win-arm64; WPF stays the Windows default.

Platform worker implements `SHOU.Launcher.Desktop.Platform.PlatformServicesFactory.Create(PlatformOptions, IVerifiedDownloader)` returning `PlatformServices`. Constructors calculate paths only. Bootstrap acquires `DataLock` before initializing any write service; disposal drains writes, stops only owned processes, then releases the lease. `--data-dir` must isolate all mutable roots, keys and lock.

UI uses `Bootstrap.Launcher` (`ILauncherService`) and `Bootstrap.ExternalLauncher`. It owns `App`, creates `MainWindow`/VM from these, and must show a clear error if host wiring is absent. Snapshot/event callbacks may originate off UI thread. Unsubscribe/dispose view models; no credentials in account DTOs or logs. UI cancellation uses CancellationToken, game stop is separate. Host owns service disposal.

`JavaInstallation` is a real probe result; arch is selected Java arch, separate from hardware/process. Installer must use official catalog, verify file size/hash, handle links/executable/bundle and only return usable after probe. `IVerifiedDownloader` is main-owned; platform may request factory injection instead of inventing downloader contracts.

`ProcessSpec.Arguments` is argv. Never shell/split. Stdout/stderr read concurrently with bounded capture and cancellation. Credential read null means absent; failures throw typed errors and must disable persistence without replacing existing secrets. Test stores are explicit.

`ILauncherService` supplies six-page production flows; tests may supply clearly named fakes. `LaunchPlan.ToRedactedReport()` excludes raw Arguments/Environment; raw plan must never be JSON serialized to a report. Account IDs and instance IDs are stable opaque IDs.

New generator owns new Core configuration dictionary, settings key registry and startup-order registry. The shared three-input capture helper is also used by the legacy generator. Exact resource name remains `PCL.metadata.json`, embedding original metadata; no fake defaults. Release/Beta/CI never read runtime PCL secrets; SHA never has runtime fallback.

Frozen for first batch. Interface change requests go to main before edits; main distributes narrow patches. Build artifacts, caches and data live below SHOU_DEV_ROOT, never in source/obj defaults. B0 is architecture/compilation evidence only, not product functionality acceptance.

B1 downloader clarification: hash-bearing requests verify before atomic replacement. Unhashed official catalog metadata is always fetched fresh, without cache hits or resume; transport validates declared size only, and the caller must parse and validate its typed catalog/target contract before trusting it. This is not runtime-installation evidence. Existing symlink/reparse-point destinations and staging sidecars are refused. Redacted LaunchPlan reports never copy argv, including user-supplied JVM arguments.
