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
# P1 protected Java and Fabric additions

ProcessSpec.SensitiveInput is optional, one-shot, bounded and excluded from JSON. The runner owns and clears consumed bytes; transfer failures stop only the owned child and never retry via argv/files. Java bootstrap protocol v1 is SHOUBOOT + big-endian version/body length + strict UTF-8 length-prefixed main/argv, max1MiB body. All game args use stdin when the packaged helper is present; real accounts fail closed when it is absent. Public JVM options may not override the trusted helper classpath/entry; the verified helper precedes game libraries. LaunchPlan.MainClassIndex is the explicit JVM/game boundary and BootstrapSha256 is public provenance.

CreateFabricInstanceAsync(name, gameVersionId, loaderVersion, ct) creates a real Fabric profile/instance. The existing Loader/LoaderVersion DTO fields are retained. Unknown loaders remain unsupported. Product RIDs remain osx-arm64/win-x64/win-arm64; linux-x64 is an explicit portable-test restore target only.


## B8 Java trust variant contract

`IJavaTrustService` and the finding/request/result DTOs are in `Abstractions/JavaTrustContracts.cs`. Source runtime trees remain read-only. Derived copies live below the caller's explicit `derived-trust` root, preserve contained relative links and executable modes, and require a completed marker, policy provenance, source/output tree fingerprints and the exact allowed certificate transformation. Original Mojang manifest checks remain mandatory; only a verified installer result carries `JavaInstallation.ValidatedManifestSha1`.

`LauncherSettings.ConduitJavaTrustAutoRepair` retains the default true preference. It automatically derives a newly installed official runtime; existing external Java requires the explicit copy action. Automatic selection creates no hidden instance override. Matching completed variants have priority only when the user has no explicit Java selection, and every selected derived runtime is revalidated even when automatic repair is disabled. Manual copy selection changes only the global default after persistence succeeds; explicit instance overrides remain.

Production `DerivedJavaTrustService` receives `IProcessRunner` for internal probes after its tree validation. Public discovery and direct probe reject incomplete/corrupt/escaping owned variants before execution; there is no public staging bypass. CLI trust modes require explicit isolated data/Java paths and do not claim GUI/game acceptance. UI scan errors/unknown findings cannot be presented as a safe result.

## Final credential failure contract

Keychain operations are noninteractive. The existing authentication-UI query flag stays in place; the legacy process-local interaction state is saved, disabled and restored inside one static synchronous gate covering every store instance. Native state/restore errors are typed failures. This changes no item ACL, user preference or keychain backend, and does not promise a hard timeout against every possible Security daemon failure. A new binary denied access must retain existing data and report denial; it must not delete/recreate the item or fall back to plaintext.

`AccountProfile.Persisted` must describe committed account state. Account/selection UI publication follows the sidecar commit. When credential reading/writing fails, new accounts remain explicitly session-only and the original disk account list/selection are preserved. If refresh already rotated the credential before a sidecar failure, keep the new valid in-memory session, mark an existing profile session-only, freeze subsequent account persistence and rethrow the real save failure; never append an unsaved new profile or reuse the old token. Cross-configuration safe denial and actual credential access continuity are separate gates.

## R40 read-only mod declarations

`IInstanceModRelationsService` is an optional additive service for the instance relationship page. It returns physical hosts, recursive declaration nodes, dependency requirements and provider paths. Presence states describe declarations found in enabled/disabled files; they do not claim loader candidate selection or version compatibility. Reads support cancellation, never extract or execute JAR contents, and report incomplete analysis explicitly. Navigation discards stale results, and mod writes await cancelled relationship readers before moving files.

## R41 resource display metadata

`IInstanceContentMetadataService` is an optional read-only enrichment service for resource packs and shaders. It verifies SHA-1/size, release/project identity and project kind before returning display names, current versions and categories; it never creates update tokens or changes instance files. The UI keeps local rows available, discards stale page results, loads local pack icons/descriptions first, and shares cancellation/draining with mod reads before any import/write.

## R42 resource pack and shader updates

`IInstanceResourceUpdateService` is additive and accepts only `resourcepacks` and `shaders`. It shares the existing file-update DTOs and backup result with mod management; `InstanceModOnlineInfo.Version` and `Requirements`, and update-result `Notices`, are optional for older callers. Checks verify installed hashes, project kind and compatible release identity, use no mod-loader filter, and issue short-lived kind-bound tokens. Automatic checks use the six-hour cache; explicit refresh bypasses it. No instance file is changed by a check.

Resource row and selected updates execute directly, while mod update warnings remain. Resource updates replace only the selected archive, as in WPF; they do not install/upgrade dependency mods. Declared requirements remain available in resource details and a completion notice. The existing download/backup/rollback transaction now supports resource kinds, preserves installed resource filenames so game enablement references survive, and backs up exact original bytes and receipts below `.shou-resource-backups`. Apply rechecks the instance, running sessions, original hashes, manifest, and selected online file. UI navigation/cancellation rejects late results; writes drain automatic and manual readers first. Unsupported older services retain read-only metadata enrichment.

## R43 local resource recovery and catalog navigation

`IInstanceContentTrashService` adds reversible file removal for `resourcepacks`, `shaders` and `schematics`. It reuses the mod trash DTO and transaction; old mod callers and `.shou-mod-trash` receipts keep their format. Other kinds store receipts in `.shou-content-trash/<physical-folder>`, bound to the instance, kind and exact file paths. Files and removed installation entries are restored together, preserving optional roles and later unrelated installs. Untracked resource files do not create an installation manifest. Restore verifies size, both hashes and literal paths, refuses occupied targets, and rolls moved files back on cancellation/failure. No permanent purge or non-mod enable/disable action is added.

The UI captures row/batch paths before confirmation, drains cancelled metadata/update readers before moves, discards stale trash reads after navigation, and scopes recovery to the current instance/category. Closing the compact recovery panel hides it without deleting receipts. Verified local mod/resource/shader details can open the existing catalog/version page with the exact instance and game version; resource/shader queries use no mod-loader filter. Back returns to the original resource section and search if its instance/directory still matches. Unknown files retain local details; an unrelated catalog open clears the return context.

## R45 screenshot, export and read-only interface parity

Screenshot enumeration, decoding, clipboard and recovery support PNG, JPG/JPEG, BMP, WebP and TIF/TIFF. Skia handles its supported formats; managed LibTiff handles TIFF. Decoders check dimensions before raster allocation, retain the existing file/pixel budgets, and never change source bytes. The gallery appends 20 at a time on scroll or an explicit load-more action with no 60-item cutoff. Navigation/disposal cancels reads and disposes previews; stale reads cannot publish or open files in another context. Existing screenshot trash receipts (including their internal payload filename) remain compatible; original file extension and SHA-256 are preserved.

`IInstanceExportSelectionService` adds read-only directory browsing, scoped to the exact instance. The UI can select whole directories or individual files, retains choices within the same instance, clears them on instance/directory identity changes, and passes explicit relative paths to the existing CF/MRpack export options. Parent selections subsume selected children. Selection does not relax export path validation, credential exclusions, archive budgets or transactional publication; no selected file is executed. `ILocalFileLauncher` is an optional additive OS opener for current screenshots and historical log files; URI opening policy is unchanged.

Instance server status uses the existing Minecraft status probe with additive optional MOTD, maximum player count, version, icon and response-duration fields. Duration includes connection/query work and is not a protocol ping/pong RTT. Status reads do not write servers.dat; UI queries are cancellable, limited to four concurrent reads and discarded after context/row changes. Only bounded PNG favicons are accepted for display, and unsupported images do not affect other server information. Rich colored MOTD rendering and protocol ping remain outside this phase. Favorite icons use existing cancellable reads and the shared image gate; favorites search is local and does not alter saved entries. Windows console-Java preference is now connected to the existing global launch option, and hidden on other platforms.
