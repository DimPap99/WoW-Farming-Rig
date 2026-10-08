using Core.Forever;

using Microsoft.Extensions.Logging.Abstractions;

using SharedLib.Forever;

using System;
using System.Threading;

using Xunit;

namespace AgentTests.Forever;

/// <summary>
/// What a <see cref="ForeverRun"/> is asked to do: each mode keeps the time bound the trial runner
/// gave it, the check modes never move, and a request missing what its mode needs is refused before
/// anything attaches to a game.
/// </summary>
public sealed class ForeverRunRequestTests
{
    private const string Root = "Json";

    [Theory]
    [InlineData(ForeverRunMode.Check, ForeverRunRequest.TrialSeconds)]
    [InlineData(ForeverRunMode.Run, ForeverRunRequest.TrialSeconds)]
    [InlineData(ForeverRunMode.AutoRun, ForeverRunRequest.AutoRunSeconds)]
    [InlineData(ForeverRunMode.Sweep, ForeverRunRequest.SweepSeconds)]
    [InlineData(ForeverRunMode.Travel, ForeverRunRequest.TravelSeconds)]
    [InlineData(ForeverRunMode.Roam, ForeverRunRequest.RoamSeconds)]
    [InlineData(ForeverRunMode.Route, ForeverRunRequest.RouteSeconds)]
    public void Each_mode_keeps_the_trial_runners_time_bound(ForeverRunMode mode, int seconds)
    {
        Assert.Equal(seconds, new ForeverRunRequest(mode) { DataRoot = Root }.TimeBoundSeconds);
    }

    [Fact]
    public void Minutes_bound_an_autonomous_run_but_not_the_route_trial()
    {
        Assert.Equal(30 * 60, new ForeverRunRequest(ForeverRunMode.Route) { DataRoot = Root, Minutes = 30 }.TimeBoundSeconds);
        Assert.Equal(90, new ForeverRunRequest(ForeverRunMode.AutoRun) { DataRoot = Root, Minutes = 1.5 }.TimeBoundSeconds);
        Assert.Equal(ForeverRunRequest.TrialSeconds, new ForeverRunRequest(ForeverRunMode.Run) { DataRoot = Root, Minutes = 30 }.TimeBoundSeconds);
    }

    [Theory]
    [InlineData(ForeverRunMode.Check, false, false)]
    [InlineData(ForeverRunMode.Run, false, true)]
    [InlineData(ForeverRunMode.AutoCheck, true, false)]
    [InlineData(ForeverRunMode.AutoRun, true, true)]
    [InlineData(ForeverRunMode.Sweep, true, true)]
    [InlineData(ForeverRunMode.Travel, true, true)]
    [InlineData(ForeverRunMode.Roam, true, true)]
    [InlineData(ForeverRunMode.Route, true, true)]
    public void Only_the_check_modes_never_move(ForeverRunMode mode, bool autonomous, bool moves)
    {
        ForeverRunRequest request = new(mode) { DataRoot = Root };

        Assert.Equal(autonomous, request.IsAutonomous);
        Assert.Equal(moves, request.Moves);
    }

    [Fact]
    public void A_request_missing_what_its_mode_needs_says_so()
    {
        Assert.Contains("point", new ForeverRunRequest(ForeverRunMode.Travel) { DataRoot = Root }.Problem);
        Assert.Contains("area", new ForeverRunRequest(ForeverRunMode.Roam) { DataRoot = Root }.Problem);
        Assert.Contains("route", new ForeverRunRequest(ForeverRunMode.Route) { DataRoot = Root }.Problem);
        Assert.Contains("minutes", new ForeverRunRequest(ForeverRunMode.AutoRun) { DataRoot = Root, Minutes = 0 }.Problem);
        Assert.Contains("minutes", new ForeverRunRequest(ForeverRunMode.AutoRun) { DataRoot = Root, Minutes = 601 }.Problem);
        Assert.Null(new ForeverRunRequest(ForeverRunMode.Route)
        {
            DataRoot = Root,
            Route = new ForeverRoute("trip", [ForeverTasks.TravelTo(new(1411, 52, 44))], false),
            Minutes = 600
        }.Problem);
    }

    // The trial runner's command line checked these itself, so an assignment of another mode would have
    // copied them: a roam with no kills completed at once, a point off its map was refused only after
    // attaching, and a travel given a route ran the route under the travel's bound.
    [Fact]
    public void A_request_holds_the_command_lines_rules()
    {
        ForeverRoute trip = new("trip", [ForeverTasks.TravelTo(new(1411, 52, 44))], false);

        Assert.Contains("off the map", new ForeverRunRequest(ForeverRunMode.Travel) { DataRoot = Root, TravelTo = (52, 101) }.Problem);
        Assert.Contains("off the map", new ForeverRunRequest(ForeverRunMode.Travel) { DataRoot = Root, TravelTo = (double.NaN, 4) }.Problem);
        Assert.Contains("off the map", new ForeverRunRequest(ForeverRunMode.Roam)
        {
            DataRoot = Root,
            Roam = new ForeverRoamRequest(-1, 0, 10, 10, 3),
        }.Problem);
        Assert.Contains("at least one kill", new ForeverRunRequest(ForeverRunMode.Roam)
        {
            DataRoot = Root,
            Roam = new ForeverRoamRequest(0, 0, 10, 10, 0),
        }.Problem);
        Assert.Contains("a route is for a route run", new ForeverRunRequest(ForeverRunMode.Travel)
        {
            DataRoot = Root,
            TravelTo = (52, 44),
            Route = trip,
        }.Problem);
        Assert.Contains("keeps its own", new ForeverRunRequest(ForeverRunMode.Run) { DataRoot = Root, Minutes = 30 }.Problem);
        Assert.Contains("minutes", new ForeverRunRequest(ForeverRunMode.Route) { DataRoot = Root, Route = trip, Minutes = double.NaN }.Problem);
        Assert.Null(new ForeverRunRequest(ForeverRunMode.Roam) { DataRoot = Root, Roam = new ForeverRoamRequest(0, 0, 100, 100, 1) }.Problem);
    }

    [Fact]
    public void A_request_with_a_problem_is_refused_before_anything_attaches()
    {
        ForeverRun run = new(new ForeverRunRequest(ForeverRunMode.Route) { DataRoot = Root }, NullLoggerFactory.Instance, new Lines());

        Assert.Throws<ArgumentException>(() => run.Run(Environment.ProcessId, CancellationToken.None));
        Assert.Equal(ForeverRunStage.NotStarted, run.Stage);
        Assert.Null(run.ExitCode);
    }

    [Fact]
    public void A_run_that_has_not_started_holds_nothing()
    {
        ForeverRun run = new(new ForeverRunRequest(ForeverRunMode.AutoCheck) { DataRoot = Root }, NullLoggerFactory.Instance, new Lines());

        Assert.Equal(ForeverRunStage.NotStarted, run.Stage);
        Assert.Null(run.ExitCode);
        Assert.Null(run.Autonomous);
        run.ReleaseInput();
    }

    private sealed class Lines : IForeverRunOutput
    {
        public void Line(string text) { }
        public void Problem(string text) { }
        public void Snapshot(string text) { }
    }
}
