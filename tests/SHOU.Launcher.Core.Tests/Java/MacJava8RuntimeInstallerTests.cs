using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Java;

namespace SHOU.Launcher.Core.Tests.Java;

[TestClass]
public sealed class MacJava8RuntimeInstallerTests
{
    [TestMethod]
    public async Task VerifiedNativeArchivePublishesAfterProbeAndReusesOnlyUnchangedFiles()
    {
        using var f=new Fixture();var java=await f.Installer.InstallAsync(f.Request);Assert.AreEqual("managed:Azul8",java.Source);Assert.AreEqual(2,f.Discovery.Calls);
        Assert.IsFalse(java.ExecutablePath.Contains(".staging",StringComparison.Ordinal));Assert.AreEqual(8,java.MajorVersion);
        var again=await f.Installer.InstallAsync(f.Request);Assert.AreEqual(java.ExecutablePath,again.ExecutablePath);Assert.AreEqual(3,f.Discovery.Calls);
        await File.WriteAllTextAsync(java.ExecutablePath,"corrupt fixture");await Assert.ThrowsAsync<InvalidDataException>(()=>f.Installer.InstallAsync(f.Request));
        Assert.AreEqual("corrupt fixture",await File.ReadAllTextAsync(java.ExecutablePath));
    }
    [TestMethod]
    public async Task CorruptArchiveAndWrongJvmArchitectureNeverPublishACompletedRuntime()
    {
        using var corrupt=new Fixture();corrupt.Downloader.Corrupt=true;await Assert.ThrowsAsync<InvalidDataException>(()=>corrupt.Installer.InstallAsync(corrupt.Request));Assert.AreEqual(0,corrupt.Discovery.Calls);
        using var wrong=new Fixture();wrong.Discovery.Arch=RuntimeArchitecture.X64;await Assert.ThrowsAsync<PlatformNotSupportedException>(()=>wrong.Installer.InstallAsync(wrong.Request));
        Assert.IsFalse(Directory.EnumerateFiles(wrong.Root,".shou-java8.json",SearchOption.AllDirectories).Any());
    }
    [TestMethod]
    public async Task EscapingArchivePathsAndLinksAreRejectedAndStagingIsRemoved()
    {
        using var traversal=new Fixture("../escape");await Assert.ThrowsAsync<IOException>(()=>traversal.Installer.InstallAsync(traversal.Request));
        using var link=new Fixture(linkTarget:"../../../../../../outside");await Assert.ThrowsAsync<InvalidDataException>(()=>link.Installer.InstallAsync(link.Request));
        Assert.IsFalse(Directory.EnumerateFiles(link.Root,".shou-java8.json",SearchOption.AllDirectories).Any());
    }
    [TestMethod,DataRow(LauncherOS.Windows,RuntimeArchitecture.X64,8),DataRow(LauncherOS.Windows,RuntimeArchitecture.Arm64,8),DataRow(LauncherOS.MacOS,RuntimeArchitecture.X64,8),DataRow(LauncherOS.MacOS,RuntimeArchitecture.Arm64,21)]
    public async Task OtherPlatformAndVersionRequestsUseTheOriginalInstallerWithoutTouchingTheArchive(LauncherOS os,RuntimeArchitecture arch,int major)
    {
        using var f=new Fixture();await f.Installer.InstallAsync(f.Request with { OS=os,Architecture=arch,MajorVersion=major });Assert.AreEqual(1,f.Fallback.Calls);Assert.AreEqual(0,f.Downloader.Calls);
    }
    [TestMethod]
    public async Task CustomMojangCatalogRemainsAnExplicitRequestRatherThanTriggeringAzulFallback()
    {
        using var f=new Fixture();await f.Installer.InstallAsync(f.Request with { CatalogUri=new("https://fixture.invalid/catalog") });Assert.AreEqual(1,f.Fallback.Calls);Assert.AreEqual(0,f.Downloader.Calls);
    }
    [TestMethod]
    public async Task ExistingLockAndStateLinksCannotRedirectTheManagedInstaller()
    {
        if (OperatingSystem.IsWindows()) Assert.Inconclusive("Unix symlink fixture.");
        using var f=new Fixture();var java=await f.Installer.InstallAsync(f.Request);
        var state=Directory.EnumerateFiles(f.Request.InstallRoot,".shou-java8.json",SearchOption.AllDirectories).Single();
        var final=Path.GetDirectoryName(state)!;
        var saved=Path.Combine(f.Root,"saved-state.json");File.Move(state,saved);File.CreateSymbolicLink(state,saved);
        await Assert.ThrowsAsync<IOException>(()=>f.Installer.InstallAsync(f.Request));File.Delete(state);File.Move(saved,state);
        var lockPath=final+".lock";File.Delete(lockPath);var target=Path.Combine(f.Root,"untouched.txt");File.WriteAllText(target,"keep");File.CreateSymbolicLink(lockPath,target);
        await Assert.ThrowsAsync<IOException>(()=>f.Installer.InstallAsync(f.Request));Assert.AreEqual("keep",File.ReadAllText(target));
    }
    private sealed class Fixture:IDisposable
    {
        public string Root=TestFiles.Root();public DownloadFake Downloader;public DiscoveryFake Discovery=new();public FallbackFake Fallback=new();public MacJava8RuntimeInstaller Installer;public JavaRuntimeRequest Request;
        public Fixture(string javaPath="jdk/Contents/Home/bin/java",string? linkTarget=null)
        {
            using var bytes=new MemoryStream();using(var gzip=new GZipStream(bytes,CompressionLevel.Fastest,true))using(var tar=new TarWriter(gzip,leaveOpen:true))
            {
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile,javaPath) { DataStream=new MemoryStream("fixture runtime"u8.ToArray()),Mode=UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute });
                if(linkTarget is not null)tar.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink,"jdk/Contents/Home/lib/escape") { LinkName=linkTarget });
            }
            var payload=bytes.ToArray();Downloader=new(payload);var runtime=Path.Combine(Root,"runtimes");Request=new("jre-legacy",8,LauncherOS.MacOS,RuntimeArchitecture.Arm64,runtime);
            Installer=new(Fallback,Downloader,Discovery,runtime,Path.Combine(Root,"cache"),new("https://cdn.azul.com/fixture.tar.gz",Convert.ToHexStringLower(SHA256.HashData(payload)),payload.Length));
        }
        public void Dispose(){if(Directory.Exists(Root))Directory.Delete(Root,true);}
    }
    private sealed class DownloadFake(byte[] bytes):IVerifiedDownloader
    {
        public bool Corrupt;public int Calls;
        public Task DownloadAsync(DownloadRequest request,IProgress<TransferProgress>? progress=null,CancellationToken cancellationToken=default)
        {Calls++;Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!);return File.WriteAllBytesAsync(request.DestinationPath,Corrupt?[1,2,3]:bytes,cancellationToken);}
    }
    private sealed class DiscoveryFake:IJavaDiscovery
    {
        public int Calls;public RuntimeArchitecture Arch=RuntimeArchitecture.Arm64;
        public Task<JavaInstallation> ProbeAsync(string executable,CancellationToken cancellationToken=default){Calls++;return Task.FromResult(new JavaInstallation(executable,Path.GetDirectoryName(Path.GetDirectoryName(executable))!,"1.8.0-fixture",8,"fixture",Arch,"fixture"));}
        public Task<IReadOnlyList<JavaInstallation>> DiscoverAsync(JavaDiscoveryRequest request,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
    }
    private sealed class FallbackFake:IJavaRuntimeInstaller
    {
        public int Calls;
        public Task<JavaInstallation> InstallAsync(JavaRuntimeRequest request,IProgress<TransferProgress>? progress=null,CancellationToken cancellationToken=default){Calls++;return Task.FromResult(new JavaInstallation("/fallback/java","/fallback","fixture",request.MajorVersion,"fixture",request.Architecture,"fixture"));}
    }
}
