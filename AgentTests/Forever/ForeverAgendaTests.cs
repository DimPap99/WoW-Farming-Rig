using Core.Forever;

using SharedLib.Forever;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Xunit;

namespace AgentTests.Forever;

/// <summary>
/// Passing the tasks already done (known issue 69): with <c>"Repeat": true</c> the agenda went round
/// to tasks met again at once (a level the character has), and the bot's thread spun for good.
/// </summary>
public sealed class ForeverAgendaTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Fact]
    public void A_repeating_agenda_whose_every_task_is_done_ends_after_one_round()
    {
        ForeverAgenda agenda = new([Named("A"), Named("B")], repeat: true);
        List<string> passed = [];

        bool ended = Pass(agenda, _ => true, passed);

        Assert.True(ended);
        Assert.True(agenda.Finished);
        Assert.Equal(["A", "B"], passed);
        Assert.Equal(1, agenda.Laps);
    }

    [Fact]
    public void Passing_stops_at_the_first_task_not_done()
    {
        ForeverAgenda agenda = new([Named("A"), Named("B"), Named("C")], repeat: true);
        List<string> passed = [];

        bool ended = Pass(agenda, task => task.Name != "B", passed);

        Assert.False(ended);
        Assert.Equal("B", agenda.Current?.Name);
        Assert.Equal(["A"], passed);
    }

    [Fact]
    public void An_agenda_that_does_not_repeat_finishes_by_itself()
    {
        ForeverAgenda agenda = new([Named("A"), Named("B")]);
        List<string> passed = [];

        bool ended = Pass(agenda, _ => true, passed);

        Assert.False(ended);
        Assert.True(agenda.Finished);
        Assert.Equal(["A", "B"], passed);
    }

    [Fact]
    public void An_ended_agenda_does_not_start_over()
    {
        ForeverAgenda agenda = new([Named("A")], repeat: true);

        agenda.End();
        agenda.Advance();

        Assert.True(agenda.Finished);
        Assert.Null(agenda.Current);
    }

    // On a thread with a deadline: the loop it replaces never returned.
    private static bool Pass(ForeverAgenda agenda, Func<ForeverTask, bool> isDone, List<string> passed)
    {
        Task<bool> passing = Task.Run(() => agenda.PassDone(isDone, (task, _) => passed.Add(task.Name)));
        Assert.True(passing.Wait(Deadline), "passing the done tasks did not end");
        return passing.Result;
    }

    private static ForeverTask Named(string name)
    {
        return ForeverTasks.HuntHere(ForeverUntil.AtLevel(10)) with { Name = name };
    }
}
