using System.Diagnostics;
using System.Text;

namespace RtcForge.Tests;

public class TaskExtensionsTests
{
    [Fact]
    public async Task FireAndForget_FaultedTask_WritesTraceError()
    {
        var listener = new CapturingTraceListener();
        Trace.Listeners.Add(listener);
        try
        {
            Task.FromException(new InvalidOperationException("boom")).FireAndForget("TestCaller");

            var completed = await Task.WhenAny(listener.Message.Task, Task.Delay(TimeSpan.FromSeconds(2)));

            Assert.Same(listener.Message.Task, completed);
            string message = await listener.Message.Task;
            Assert.Contains("TestCaller", message);
            Assert.Contains("boom", message);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    private sealed class CapturingTraceListener : TraceListener
    {
        private readonly StringBuilder _buffer = new();
        public TaskCompletionSource<string> Message { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Write(string? message)
        {
            if (!string.IsNullOrEmpty(message))
            {
                Append(message);
            }
        }

        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrEmpty(message))
            {
                Append(message);
            }
        }

        private void Append(string message)
        {
            _buffer.Append(message);
            var current = _buffer.ToString();
            if (current.Contains("boom"))
            {
                Message.TrySetResult(current);
            }
        }
    }
}
