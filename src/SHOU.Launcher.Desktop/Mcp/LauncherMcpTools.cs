using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SHOU.Launcher.Core;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.ViewModels;

namespace SHOU.Launcher.Desktop.Mcp;

[McpServerToolType]
internal sealed class LauncherMcpTools(LauncherService launcher)
{
    [McpServerTool(Name = "list_instances", ReadOnly = true, OpenWorld = false)]
    [Description("List registered Minecraft instances, their versions, game directories, and running state. Use the returned instance IDs in other tools.")]
    public object ListInstances()
    {
        var snapshot = launcher.Snapshot;
        return new
        {
            selectedInstanceId = snapshot.SelectedInstanceId,
            instances = snapshot.Instances.Select(instance => new
            {
                instance = Describe(instance),
                running = snapshot.GameSessions.Any(session => session.InstanceId == instance.Id)
            }).ToArray()
        };
    }

    [McpServerTool(Name = "inspect_game_directory", ReadOnly = true, OpenWorld = false)]
    [Description("Inspect a user-selected local Minecraft root, versions folder, or version folder for attachable existing instances.")]
    public Task<object> InspectGameDirectory(string directory, CancellationToken cancellationToken) => CallAsync(async () =>
    {
        RequireAbsolute(directory);
        var result = await launcher.InspectInstanceFolderDetailedAsync(directory, cancellationToken);
        return new
        {
            candidates = result.Candidates.Select(candidate => new { candidate.Name, candidate.VersionId, candidate.GameVersion,
                candidate.Loader, candidate.LoaderVersion, candidate.GameDirectory, candidate.Origin.VersionDirectory }),
            result.Issues
        };
    });

    [McpServerTool(Name = "attach_game_directory", Destructive = false, OpenWorld = false)]
    [Description("Register one existing Minecraft version directory returned by inspect_game_directory. The original game files stay in place.")]
    public Task<object> AttachGameDirectory(string versionDirectory, string versionId, CancellationToken cancellationToken) => CallAsync(async () =>
    {
        RequireAbsolute(versionDirectory);
        var candidates = await launcher.InspectInstanceFolderAsync(versionDirectory, cancellationToken);
        var target = candidates.SingleOrDefault(candidate => candidate.VersionId == versionId &&
            string.Equals(Path.GetFullPath(versionDirectory), candidate.Origin.VersionDirectory,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            ?? throw new McpException("The selected folder no longer identifies this version. Run inspect_game_directory again.");
        return Describe(await launcher.AttachExistingInstanceAsync(target, cancellationToken));
    });

    [McpServerTool(Name = "select_instance", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Select a registered instance in the launcher.")]
    public Task<object> SelectInstance(string instanceId, CancellationToken cancellationToken) => CallAsync(async () =>
    {
        await launcher.SelectInstanceAsync(instanceId, cancellationToken);
        return new { instanceId };
    });

    [McpServerTool(Name = "rename_instance", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Rename a stopped instance in the launcher.")]
    public Task<object> RenameInstance(string instanceId, string name, CancellationToken cancellationToken) => CallAsync(async () =>
    {
        await launcher.RenameInstanceAsync(instanceId, name, cancellationToken);
        return Describe(Instance(instanceId));
    });

    [McpServerTool(Name = "set_game_directory_mode", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Change a stopped instance's game directory mode. Existing files stay in their original directory.")]
    public Task<object> SetGameDirectoryMode(string instanceId, [Description("current, followGlobal, isolated, or shared")] string mode,
        [Description("True after the user confirms changing the active directory without moving files.")] bool confirmed,
        CancellationToken cancellationToken) => CallAsync(async () =>
    {
        var directoryMode = mode switch
        {
            "current" => InstanceDirectoryMode.Current, "followGlobal" => InstanceDirectoryMode.FollowGlobal,
            "isolated" => InstanceDirectoryMode.Isolated, "shared" => InstanceDirectoryMode.Shared,
            _ => throw new McpException("Use current, followGlobal, isolated, or shared.")
        };
        await launcher.SetInstanceDirectoryModeAsync(instanceId, directoryMode, confirmed, cancellationToken);
        return Describe(Instance(instanceId));
    });

    [McpServerTool(Name = "list_minecraft_versions", ReadOnly = true)]
    [Description("Read the Minecraft version catalog. Filter by version ID substring or kind and page through results.")]
    public Task<object> ListMinecraftVersions(CancellationToken cancellationToken, string query = "", string? kind = null, int offset = 0) => CallAsync(async () =>
    {
        CheckOffset(offset);
        var catalog = await launcher.GetGameCatalogAsync(cancellationToken);
        var versions = catalog.Versions.Where(version => (kind is null || version.Kind == kind) &&
            version.Id.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        return new { catalog.Source, catalog.Partial, catalog.Notice, total = versions.Length, offset, versions = versions.Skip(offset).Take(50) };
    });

    [McpServerTool(Name = "list_loader_versions", ReadOnly = true)]
    [Description("List compatible loader builds for an exact Minecraft version. The loader ID is the one used in the launcher's installation catalog.")]
    public Task<object> ListLoaderVersions(string gameVersion, string loader, CancellationToken cancellationToken, int offset = 0) => CallAsync(async () =>
    {
        CheckOffset(offset);
        var versions = await launcher.GetLoaderVersionsAsync(loader, gameVersion, cancellationToken);
        return new { total = versions.Count, offset, versions = versions.Skip(offset).Take(50) };
    });

    [McpServerTool(Name = "install_minecraft_version", Destructive = false)]
    [Description("Create and install a new Minecraft instance and its dependencies, including a compatible Java runtime when required. This does not launch Minecraft.")]
    public Task<object> InstallMinecraftVersion(string name, string gameVersion, CancellationToken cancellationToken,
        string loader = "vanilla", string? loaderVersion = null) => CallAsync(async () =>
        Describe(await launcher.CreateInstalledInstanceAsync(name, gameVersion, loader, loaderVersion, cancellationToken)));

    [McpServerTool(Name = "prepare_instance", Destructive = false)]
    [Description("Verify and prepare the game files and required Java for a registered stopped instance. This does not launch Minecraft.")]
    public Task<object> PrepareInstance(string instanceId, CancellationToken cancellationToken) => CallAsync(async () =>
    {
        await launcher.InstallInstanceAsync(instanceId, cancellationToken);
        return Describe(Instance(instanceId));
    });

    [McpServerTool(Name = "search_resources", ReadOnly = true)]
    [Description("Search the launcher's Modrinth or CurseForge catalog. Supported kinds are modpack, mod, resourcepack, shader, datapack, and world.")]
    public Task<object> SearchResources(string kind, string query, CancellationToken cancellationToken, string source = "all",
        string? gameVersion = null, string? loader = null, int offset = 0) => CallAsync(async () =>
    {
        CheckOffset(offset);
        var result = await launcher.SearchContentPageAsync(new(kind, query, gameVersion, loader, Offset: offset, Source: source), cancellationToken);
        return new { result.Notice, offset, projects = result.Projects.Select(p => new { p.Id, p.Title, p.Kind, p.Source, p.PageUri }) };
    });

    [McpServerTool(Name = "list_resource_versions", ReadOnly = true)]
    [Description("List releases and exact filenames for a catalog project ID. Set a game version or loader filter to check compatibility.")]
    public Task<object> ListResourceVersions(string projectId, CancellationToken cancellationToken, string? gameVersion = null,
        string? loader = null, int offset = 0) => CallAsync(async () =>
    {
        CheckOffset(offset);
        var releases = await launcher.GetContentReleasesAsync(projectId, gameVersion, loader, cancellationToken);
        return new { total = releases.Count, offset, releases = releases.Skip(offset).Take(30).Select(release => new
        {
            release.Id, release.Name, release.Version, release.GameVersions, release.Loaders, release.Notice,
            manualFileName = release.ManualFile?.FileName,
            files = release.Files.Select(file => new { file.FileName, file.Size, file.Primary, manual = file.ManualFile is not null })
        }) };
    });

    [McpServerTool(Name = "download_resource", Destructive = false, Idempotent = true)]
    [Description("Download and verify an exact catalog file into the launcher's cache. Works for modpacks, resource packs, mods and other catalog items.")]
    public Task<object> DownloadResource(string projectId, string releaseId, string fileName, CancellationToken cancellationToken) => CallAsync(async () =>
    {
        var file = await ResolveFileAsync(projectId, releaseId, fileName, cancellationToken);
        return new { path = await launcher.DownloadContentAsync(file, cancellationToken), file.Sha1, file.Size };
    });

    [McpServerTool(Name = "install_instance_resource", Destructive = false)]
    [Description("Download and install a catalog mod, resourcepack or shader into a stopped instance using the launcher compatibility and dependency checks.")]
    public Task<object> InstallInstanceResource(string instanceId, string kind, string projectId, string releaseId,
        string fileName, CancellationToken cancellationToken) => CallAsync(async () =>
    {
        var result = await launcher.InstallContentAsync(new(instanceId, projectId, releaseId, fileName, kind), cancellationToken);
        return new { result.InstanceId, result.AddedFiles, result.Warnings, paths = result.Files.Select(file => file.RelativePath) };
    });

    [McpServerTool(Name = "install_modpack", Destructive = false)]
    [Description("Install a catalog modpack as a new instance after verifying the archive and required files. This does not launch Minecraft.")]
    public Task<object> InstallModpack(string projectId, string releaseId, string fileName, string name,
        CancellationToken cancellationToken, bool prepareGameFiles = false) => CallAsync(async () =>
    {
        var project = await launcher.GetContentProjectAsync(projectId, cancellationToken);
        if (project.Kind != "modpack") throw new McpException("The selected project is not a modpack.");
        var file = await ResolveFileAsync(project.Id, releaseId, fileName, cancellationToken);
        var archive = await launcher.DownloadContentAsync(file, cancellationToken);
        var inspection = await launcher.InspectLocalPackAsync(archive, cancellationToken);
        if (!inspection.CanImport) throw new McpException(UiPrivacy.Redact(inspection.CapabilityReason ?? "The pack cannot be imported. See the desktop Downloads page."));
        if (inspection.ManualFiles is { Count: > 0 }) throw new McpException("This pack requires manual files. Open the archive through Downloads in the launcher to supply them.");
        var result = await launcher.ImportLocalPackAsync(new(archive, inspection.ArchiveSha256, name, PrepareAfterImport: prepareGameFiles), cancellationToken);
        return new { instance = Describe(result.Instance), result.InstalledFiles, result.Warnings };
    });

    [McpServerTool(Name = "discover_java", ReadOnly = true, OpenWorld = false)]
    [Description("Scan and probe Java installations known to the launcher. Return usable runtimes and current global or instance selection.")]
    public Task<object> DiscoverJava(CancellationToken cancellationToken, string? instanceId = null) => CallAsync(async () =>
    {
        var instance = instanceId is null ? null : Instance(instanceId);
        var runtimes = await launcher.DiscoverJavaAsync(cancellationToken);
        return new
        {
            defaultExecutable = launcher.Snapshot.Settings.JavaExecutable,
            instanceExecutable = instance?.JavaExecutable,
            runtimes = runtimes.Where(java => java.IsUsable).Select(java => new
                { java.ExecutablePath, java.FullVersion, java.MajorVersion, architecture = java.RuntimeArchitecture.ToString(), java.Vendor })
        };
    });

    [McpServerTool(Name = "set_java", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Set the default or instance Java using a usable executable from discover_java. Omit executable to return to automatic selection or inheritance.")]
    public Task<object> SetJava(CancellationToken cancellationToken, string? executable = null, string? instanceId = null) => CallAsync(async () =>
    {
        if (instanceId is null)
            await launcher.SetDefaultJavaAsync(executable, cancellationToken);
        else
            await launcher.SetInstanceJavaAsync(instanceId, executable, cancellationToken);
        return new { instanceId, executable = instanceId is null ? launcher.Snapshot.Settings.JavaExecutable : Instance(instanceId).JavaExecutable };
    });

    [McpServerTool(Name = "analyze_latest_crash", ReadOnly = true, OpenWorld = false)]
    [Description("Analyze the latest crash for a registered Minecraft instance. Return redacted findings, evidence and troubleshooting suggestions from the launcher.")]
    public Task<object> AnalyzeLatestCrash(string instanceId, CancellationToken cancellationToken, string? language = null) => CallAsync(async () =>
    {
        var report = await launcher.AnalyzeLatestInstanceCrashAsync(instanceId, language ?? launcher.Snapshot.Settings.Language, cancellationToken);
        return new
        {
            instanceId,
            files = report.Files.Select(path => Path.GetFileName(path)),
            advice = UiPrivacy.Redact(report.Text),
            findings = report.Findings.Select(finding => new { finding.Cause, finding.Confidence,
                evidence = finding.Evidence.Take(10).Select(line => UiPrivacy.Redact(line[..Math.Min(line.Length, 800)])) }),
            report.SuggestInstanceSettings
        };
    });

    private GameInstance Instance(string id) => launcher.Snapshot.Instances.SingleOrDefault(instance => instance.Id == id)
        ?? throw new McpException("Instance not found. Run list_instances and use the current instance ID.");

    private static object Describe(GameInstance instance) => new
    {
        instance.Id, instance.Name, instance.VersionId, gameVersion = instance.GameVersionId ?? instance.VersionId,
        instance.Loader, instance.LoaderVersion, instance.Installed, instance.GameDirectory,
        directoryMode = instance.Directories?.Mode.ToString() ?? "Current"
    };

    private async Task<ContentFile> ResolveFileAsync(string projectId, string releaseId, string fileName, CancellationToken cancellationToken)
    {
        var release = await launcher.GetContentReleaseAsync(releaseId, cancellationToken);
        if (release.Id != releaseId || release.ProjectId != projectId)
            throw new McpException("The release does not belong to this project.");
        if (release.ManualFile?.FileName == fileName)
            throw new McpException("This file requires a manual download through the launcher Downloads page.");
        var file = release.Files.SingleOrDefault(candidate => candidate.FileName == fileName);
        if (file is null) throw new McpException("File not found. Use the exact filename from list_resource_versions.");
        if (file.ManualFile is not null) throw new McpException("This file requires a manual download through the launcher Downloads page.");
        return file;
    }

    private static void RequireAbsolute(string directory)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new McpException("Use an absolute path to the existing game directory.");
    }

    private static void CheckOffset(int offset)
    {
        if (offset is < 0 or > 10_000) throw new McpException("Offset must be between 0 and 10000.");
    }

    private static async Task<object> CallAsync(Func<Task<object>> action)
    {
        try { return await action().ConfigureAwait(false); }
        catch (HttpRequestException) { throw new McpException("The launcher could not reach the catalog or download provider. Check the download settings and retry."); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or InvalidDataException or NotSupportedException)
        { throw new McpException(UiPrivacy.Redact(ex.Message)); }
    }
}
