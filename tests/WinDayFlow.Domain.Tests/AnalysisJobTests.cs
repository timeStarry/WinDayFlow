using Xunit;

namespace WinDayFlow.Domain.Tests;

public sealed class AnalysisJobTests
{
    private static readonly Guid JobId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ProviderId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset Now = new(2026, 7, 23, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PendingFactoryCreatesEligibleUnleasedJob()
    {
        var job = CreatePending();

        Assert.Equal(AnalysisJobState.Pending, job.State);
        Assert.Equal(0, job.Attempt);
        Assert.Equal(Now, job.NotBeforeUtc);
        Assert.Null(job.Lease);
        Assert.Null(job.Failure);
        Assert.Null(job.CompletedAtUtc);
    }

    [Theory]
    [InlineData(AnalysisJobState.Pending, AnalysisJobState.Claimed, true)]
    [InlineData(AnalysisJobState.Pending, AnalysisJobState.Cancelled, true)]
    [InlineData(AnalysisJobState.Claimed, AnalysisJobState.Extracting, true)]
    [InlineData(AnalysisJobState.Extracting, AnalysisJobState.Observing, true)]
    [InlineData(AnalysisJobState.Observing, AnalysisJobState.Summarizing, true)]
    [InlineData(AnalysisJobState.Summarizing, AnalysisJobState.Committing, true)]
    [InlineData(AnalysisJobState.Committing, AnalysisJobState.Completed, true)]
    [InlineData(AnalysisJobState.Observing, AnalysisJobState.FailedRetryable, true)]
    [InlineData(AnalysisJobState.Observing, AnalysisJobState.FailedTerminal, true)]
    [InlineData(AnalysisJobState.Pending, AnalysisJobState.Completed, false)]
    [InlineData(AnalysisJobState.Completed, AnalysisJobState.Pending, false)]
    [InlineData(AnalysisJobState.FailedTerminal, AnalysisJobState.Claimed, false)]
    public void StateMachineAllowsOnlyDocumentedTransitions(
        AnalysisJobState current,
        AnalysisJobState next,
        bool expected)
    {
        Assert.Equal(expected, AnalysisJobStateMachine.CanTransition(current, next));
    }

    [Fact]
    public void ActiveStateRequiresMatchingLease()
    {
        Assert.Throws<ArgumentException>(() => Restore(
            AnalysisJobState.Extracting,
            attempt: 1,
            notBefore: null,
            lease: null,
            failure: null,
            completed: null));

        var wrongJobLease = new AnalysisJobLease(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            "worker-a",
            new string('a', 32),
            1,
            Now.AddMinutes(1));
        Assert.Throws<ArgumentException>(() => Restore(
            AnalysisJobState.Extracting,
            attempt: 1,
            notBefore: null,
            wrongJobLease,
            failure: null,
            completed: null));
    }

    [Fact]
    public void RetryableFailureRequiresRemainingAttemptAndFailureCode()
    {
        var failure = new AnalysisJobFailure(AnalysisJobErrorCode.ProviderUnavailable);
        Assert.Throws<ArgumentException>(() => Restore(
            AnalysisJobState.FailedRetryable,
            attempt: 3,
            notBefore: Now.AddMinutes(1),
            lease: null,
            failure,
            completed: null));

        Assert.Throws<ArgumentException>(() => Restore(
            AnalysisJobState.FailedRetryable,
            attempt: 1,
            notBefore: Now.AddMinutes(1),
            lease: null,
            failure: null,
            completed: null));
    }

    [Theory]
    [InlineData(AnalysisJobState.Pending, 0)]
    [InlineData(AnalysisJobState.Claimed, 1)]
    [InlineData(AnalysisJobState.Extracting, 2)]
    [InlineData(AnalysisJobState.Observing, 3)]
    [InlineData(AnalysisJobState.Summarizing, 4)]
    [InlineData(AnalysisJobState.Committing, 5)]
    [InlineData(AnalysisJobState.Completed, 6)]
    [InlineData(AnalysisJobState.FailedRetryable, 7)]
    [InlineData(AnalysisJobState.FailedTerminal, 8)]
    [InlineData(AnalysisJobState.Cancelled, 9)]
    public void StateValuesAreStable(AnalysisJobState state, int expected)
    {
        Assert.Equal(expected, (int)state);
    }

    private static AnalysisJob CreatePending() => AnalysisJob.CreatePending(
        JobId,
        "chunk-safe",
        ProviderId,
        providerProfileRevision: 1,
        "analysis-v1",
        new string('A', 64),
        maxAttempts: 3,
        Now);

    private static AnalysisJob Restore(
        AnalysisJobState state,
        int attempt,
        DateTimeOffset? notBefore,
        AnalysisJobLease? lease,
        AnalysisJobFailure? failure,
        DateTimeOffset? completed)
    {
        return new AnalysisJob(
            JobId,
            "chunk-safe",
            ProviderId,
            providerProfileRevision: 1,
            "analysis-v1",
            new string('A', 64),
            state,
            attempt,
            maxAttempts: 3,
            notBefore,
            lease,
            failure,
            Now,
            Now,
            completed);
    }
}
