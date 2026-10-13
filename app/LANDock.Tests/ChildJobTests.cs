using System.Diagnostics;
using LANDock.Core;
using Xunit;

namespace LANDock.Tests;

public class ChildJobTests
{
    [Fact]
    public void A_helper_in_the_job_ends_when_the_job_is_closed()
    {
        var job = new ChildJob();
        var p = Process.Start(new ProcessStartInfo("ping", "-n 120 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            Assert.True(job.Add(p));
            Assert.False(p.HasExited);
            job.Dispose();   // the last handle of the job: the processes in it end
            Assert.True(p.WaitForExit(10_000));
        }
        finally
        {
            if (!p.HasExited) p.Kill(true);
            p.Dispose();
        }
    }
}
