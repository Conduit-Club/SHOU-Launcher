namespace SHOU.Launcher.Core.Downloads;

internal sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
{ public void Report(T value) => callback(value); }
