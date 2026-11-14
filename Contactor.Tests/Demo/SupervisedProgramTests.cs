using Contactor.Demo;
using Contactor.Tests.Web;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contactor.Tests.Demo;

/// <summary>A copy's program kept running: started again when it exits by itself, given up when it can't start.</summary>
public sealed class SupervisedProgramTests : IAsyncDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);

    private readonly FakeStarter _starter = new();
    private readonly ManualTimeProvider _time = new();
    private readonly SupervisedProgram _program;

    public SupervisedProgramTests()
    {
        var start = new ProgramStart("app", "0123ab", "/demo/app/Contactor.Web.dll", 41001, new Dictionary<string, string> { ["A"] = "1" });
        _program = new SupervisedProgram(start, _starter, StartTimeout, _time, NullLogger.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _program.DisposeAsync();
    }

    [Fact]
    public async Task Should_StartTheProgramAgain_WhenItExitsByItself_WithTheSameSettings()
    {
        _program.Start();
        await _program.Listening;
        var first = Assert.Single(_starter.Runs);

        // The app stops by itself, e.g. after the setup guide's board choice.
        first.Exit(0);

        await Wait.UntilAsync(() => _starter.Runs.Count == 2);
        var second = _starter.Runs[1];
        Assert.Same(first.Start, second.Start);
        await _program.Listening;
        Assert.False(second.HasExited);
        Assert.False(_program.GaveUp.IsCompleted);
        Assert.Equal(2, _program.Starts);
    }

    [Fact]
    public async Task Should_LetRequestsWait_ForTheRestartedProgram()
    {
        _starter.Listens = false;
        _program.Start();
        await Wait.UntilAsync(() => _starter.Runs.Count == 1);
        _starter.Runs[0].Listen();
        await _program.Listening;

        _starter.Runs[0].Exit(0);
        await Wait.UntilAsync(() => _starter.Runs.Count == 2);
        var waiting = _program.Listening;
        Assert.False(waiting.IsCompleted);

        _starter.Runs[1].Listen();
        await waiting;
    }

    [Fact]
    public async Task Should_GiveUp_WhenItExitsBeforeListening_ThreeTimesInARow()
    {
        _starter.Listens = false;
        _program.Start();
        for (var i = 1; i <= SupervisedProgram.MaxFailedStarts; i++)
        {
            await Wait.UntilAsync(() => _starter.Runs.Count == i);
            _starter.Runs[^1].Exit(134);
        }

        await _program.GaveUp;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _program.Listening);
        Assert.Equal(SupervisedProgram.MaxFailedStarts, _starter.Runs.Count);
    }

    [Fact]
    public async Task Should_GiveUp_WhenItDoesNotListenInTime()
    {
        _starter.Listens = false;
        _program.Start();
        await Wait.UntilAsync(() => _starter.Runs.Count == 1 && _time.PendingTimers > 0);

        _time.Advance(StartTimeout);

        await _program.GaveUp;
        Assert.True(_starter.Runs[0].Stopped);
        Assert.Single(_starter.Runs);
    }

    [Fact]
    public async Task Should_StopTheProgram_AndStartItNoMore()
    {
        _program.Start();
        await _program.Listening;

        await _program.StopAsync();

        var run = Assert.Single(_starter.Runs);
        Assert.True(run.Stopped);
        Assert.False(_program.GaveUp.IsCompleted);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _program.Listening);
    }
}
