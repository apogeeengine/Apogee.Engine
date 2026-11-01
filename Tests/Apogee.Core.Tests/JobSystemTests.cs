using System;
using System.Threading;
using Apogee.Core.Concurrency;
using Xunit;

namespace Apogee.Core.Tests;

public class JobSystemTests
{
    [Fact]
    public void ParallelForVisitsEveryIndexOnce()
    {
        using var jobs = new JobSystem(4);
        var visits = new int[10_000];

        jobs.ParallelFor(visits.Length, 64, (start, end) =>
        {
            for (int i = start; i < end; i++)
            {
                Interlocked.Increment(ref visits[i]);
            }
        });

        Assert.All(visits, v => Assert.Equal(1, v));
    }

    [Fact]
    public void GroupWaitReturnsAfterAllJobs()
    {
        using var jobs = new JobSystem(2);
        using var group = new JobGroup();
        int done = 0;

        for (int i = 0; i < 100; i++)
        {
            jobs.Schedule(group, () =>
            {
                Thread.Sleep(1);
                Interlocked.Increment(ref done);
            });
        }

        group.Wait();

        Assert.Equal(100, done);
    }

    [Fact]
    public void WaitRethrowsAJobsException()
    {
        using var jobs = new JobSystem(2);
        using var group = new JobGroup();
        jobs.Schedule(group, () => throw new InvalidOperationException("boom"));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(group.Wait);

        Assert.Equal("boom", error.Message);
    }

    [Fact]
    public void EmptyGroupDoesNotBlock()
    {
        using var group = new JobGroup();

        group.Wait();
    }
}
