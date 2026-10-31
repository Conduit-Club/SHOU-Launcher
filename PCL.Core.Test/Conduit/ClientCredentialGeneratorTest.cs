using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.SourceGenerators;

namespace PCL.Core.Test.Conduit;

[TestClass]
[DoNotParallelize]
public sealed class ClientCredentialGeneratorTest
{
    private const string ApiKey = "test-distributable-curseforge-project-key";
    private const string OtherSecret = "unrelated-private-server-token-must-not-be-embedded";
    private const string ClientId = "11111111-2222-3333-4444-555555555555";
    private const string SourceSha = "test-source-sha";

    [TestMethod]
    public void ConfiguredClientValuesAreEmittedAndUnrelatedCredentialsAreExcluded()
    {
        WithBuildEnvironment(true, () =>
        {
            var (source, compilation) = Generate();
            StringAssert.Contains(source, ClientId);
            StringAssert.Contains(source, SourceSha);
            StringAssert.Contains(source, ApiKey);
            Assert.IsFalse(source.Contains(OtherSecret, StringComparison.Ordinal));
            Assert.IsFalse(source.Contains("OTHER_SERVICE_TOKEN", StringComparison.Ordinal));
            using var assembly = new MemoryStream();
            var result = compilation.Emit(assembly);
            Assert.IsTrue(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            var bytes = assembly.ToArray();
            Assert.IsTrue(((ReadOnlySpan<byte>)bytes).IndexOf(Encoding.Unicode.GetBytes(ApiKey)) >= 0);
            Assert.IsTrue(((ReadOnlySpan<byte>)bytes).IndexOf(Encoding.Unicode.GetBytes(OtherSecret)) < 0);
        });
    }

    [TestMethod]
    public void DisabledGenerationDoesNotCaptureEnvironmentValues()
    {
        WithBuildEnvironment(false, () =>
        {
            var (source, _) = Generate();
            foreach (var value in new[] { ClientId, SourceSha, ApiKey, OtherSecret })
                Assert.IsFalse(source.Contains(value, StringComparison.Ordinal));
        });
    }

    private static (string Source, Compilation Compilation) Generate()
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("ClientCredentialGeneratorTest",
            [CSharpSyntaxTree.ParseText("namespace PCL.Core.Utils.OS { public static partial class EnvironmentInterop { } }")],
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new EnvironmentInteropGenerator().AsSourceGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        Assert.IsFalse(diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error));
        return (driver.GetRunResult().Results.Single().GeneratedSources.Single().SourceText.ToString(), output);
    }

    private static void WithBuildEnvironment(bool enabled, Action test)
    {
        var values = new Dictionary<string, string?>
        {
            ["PCL_WRITE_SECRET"] = enabled ? "1" : null,
            ["PCL_MS_CLIENT_ID"] = ClientId,
            ["PCL_GITHUB_SHA"] = SourceSha,
            ["PCL_CURSEFORGE_API_KEY"] = ApiKey,
            ["PCL_OTHER_SERVICE_TOKEN"] = OtherSecret
        };
        var saved = values.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var pair in values) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            test();
        }
        finally
        {
            foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }
}
