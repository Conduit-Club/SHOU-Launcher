using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core.Tests.Platform;

internal sealed class PlatformFixture
{
    public string Root { get; }
    public string JavaSource { get; }
    public static string JavaExecutable
    {
        get
        {
            var home = Environment.GetEnvironmentVariable("JAVA_HOME")
                ?? throw new InvalidOperationException("Native platform tests require the controlled JAVA_HOME supplied by control/dev.py.");
            var executable = Path.Combine(home, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
            if (!File.Exists(executable)) throw new FileNotFoundException("The controlled test Java executable is missing.", executable);
            return executable;
        }
    }

    public PlatformFixture()
    {
        var taskRoot = Environment.GetEnvironmentVariable("SHOU_DEV_ROOT")
            ?? throw new InvalidOperationException("Tests must run through the registered task environment.");
        Root = Path.Combine(taskRoot, "test-data", "platform", "B1", "中文 空格 (fixture)", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        JavaSource = Path.Combine(Root, "ProcessFixture.java");
        File.WriteAllText(JavaSource, """
            public class ProcessFixture {
                public static void main(String[] args) throws Exception {
                    switch (args[0]) {
                        case "argv":
                            for (int i = 1; i < args.length; i++) System.out.print(args[i].length() + ":" + args[i]);
                            break;
                        case "flood":
                            for (int i = 0; i < 4096; i++) { System.out.print("O".repeat(128)); System.err.print("E".repeat(128)); }
                            break;
                        case "sleep":
                            System.out.println("started"); System.out.flush(); Thread.sleep(30000); break;
                        case "env": System.out.print(System.getenv(args[1])); break;
                        default: throw new IllegalArgumentException("Unknown fixture mode");
                    }
                }
            }
            """);
    }

    public ProcessSpec JavaProcess(string mode, params string[] arguments) => new(JavaExecutable,
        new[] { JavaSource, mode }.Concat(arguments).ToArray(), Root, Timeout: TimeSpan.FromSeconds(20));
    public string Child(string name) => Path.Combine(Root, name);
    public static PlatformContext Context(LauncherOS os) => new(os, RuntimeArchitecture.Arm64, RuntimeArchitecture.Arm64, "fixture");
}

internal sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
{
    public void Report(T value) => action(value);
}
