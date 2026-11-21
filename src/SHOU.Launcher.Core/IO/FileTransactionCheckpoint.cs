using System.Runtime.CompilerServices;
[assembly: InternalsVisibleTo("SHOU.Launcher.Core.Tests")]
namespace SHOU.Launcher.Core.IO;

internal static class FileTransactionCheckpoint
{
    private static readonly AsyncLocal<Action<string>?> observer = new();
    internal static IDisposable Observe(Action<string> action)
    {
        var previous = observer.Value; observer.Value = action;
        return new Scope(() => observer.Value = previous);
    }
    internal static void Reach(string phase) => observer.Value?.Invoke(phase);
    private sealed class Scope(Action dispose) : IDisposable { public void Dispose() => dispose(); }
}
