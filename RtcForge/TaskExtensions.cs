using System.Runtime.CompilerServices;

namespace RtcForge;

internal static class TaskExtensions
{
    public static void FireAndForget(this Task task, [CallerMemberName] string? caller = null)
    {
        task.ContinueWith(
            static (t, state) => System.Diagnostics.Trace.TraceError(
                $"[RtcForge] Unobserved exception in {state}: {t.Exception!.Flatten().InnerException}"),
            caller,
            TaskContinuationOptions.OnlyOnFaulted);
    }
}
