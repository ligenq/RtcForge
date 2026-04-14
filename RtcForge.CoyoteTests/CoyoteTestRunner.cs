using Microsoft.Coyote;
using Microsoft.Coyote.SystematicTesting;
using Xunit.Abstractions;

namespace RtcForge.CoyoteTests;

internal sealed class CoyoteTestRunner
{
    private readonly ITestOutputHelper _output;

    public CoyoteTestRunner(ITestOutputHelper output)
    {
        _output = output;
    }

    public void Run(Func<Task> test, uint iterations = 50)
    {
        var configuration = Configuration.Create()
            .WithTestingIterations(iterations)
            .WithMaxSchedulingSteps(200u)
            .WithNoBugTraceRepro();

        using var engine = TestingEngine.Create(configuration, test);
        engine.Run();

        var report = engine.TestReport;
        foreach (string bug in report.BugReports)
        {
            _output.WriteLine(bug);
        }

        Assert.True(report.NumOfFoundBugs == 0, $"Coyote found {report.NumOfFoundBugs} bug(s).");
    }
}
