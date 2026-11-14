using System.Text;

namespace SHOU.Launcher.Core.Tests.Platform;

/// <summary>Explicit child-process entry point for the generic Unicode argv transport test, not a game.</summary>
internal static class ProcessArgumentFixture
{
    public static int Main(string[] arguments)
    {
        if (arguments.Length == 0 || arguments[0] != "--argv-fixture") return 2;
        Console.OutputEncoding = new UTF8Encoding(false);
        foreach (var value in arguments.Skip(1)) Console.Write(value.Length + ":" + value);
        return 0;
    }
}
