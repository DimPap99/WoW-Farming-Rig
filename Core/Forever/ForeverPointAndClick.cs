using Game;

using static System.Diagnostics.Stopwatch;

namespace Core.Forever;

public enum ForeverClickResult { Pointing, Done, Failed }

/// <summary>
/// Something a <see cref="ForeverPointAndClick"/> aims at, read from each
/// fresh frame: a nameplate today, a corpse or an NPC tomorrow.
/// </summary>
public interface IForeverClickTarget
{
    /// <summary>What it is, for the reasons a failure gives.</summary>
    string Name { get; }

    /// <summary>It is no longer there to click, so pointing at it ends.</summary>
    bool IsGone(VisualBarSample bars);

    /// <summary>Where it shows in this frame; null while it is not visible.</summary>
    VisualScreenPointSample? Where(VisualBarSample bars);

    /// <summary>The game reports it under the cursor.</summary>
    bool IsHovered(VisualBarSample bars);

    /// <summary>The click took effect, for example it became the target.</summary>
    bool IsDone(VisualBarSample bars);
}

/// <summary>
/// Points and clicks the way a person does, for anything on screen: the
/// cursor goes onto the thing in one swift eased stroke, the click comes only
/// while the game reports the thing under the cursor, and it counts once the
/// game shows its effect. A click without effect is retried once.
/// </summary>
public sealed class ForeverPointAndClick(IMousePointer pointer)
{
    // A click the game has not answered within this long is retried.
    private const int ClickSettleMs = 1000;
    private const int MaxClicks = 2;
    // Aiming and clicking gets this long.
    private const int BoundMs = 5000;

    private IForeverClickTarget? target;
    private int clicks;
    private long began;
    private long lastClick;

    public void Begin(IForeverClickTarget clickTarget)
    {
        target = clickTarget;
        clicks = 0;
        lastClick = 0;
        began = GetTimestamp();
    }

    /// <summary>One step on a fresh frame of the click begun with <see cref="Begin"/>.</summary>
    public ForeverClickResult Update(VisualBarSample bars, out string reason)
    {
        reason = string.Empty;
        if (target is null)
        {
            reason = "nothing to click";
            return ForeverClickResult.Failed;
        }
        if (target.IsGone(bars))
        {
            reason = $"{target.Name} is gone";
            return ForeverClickResult.Failed;
        }
        if (target.IsDone(bars))
            return ForeverClickResult.Done;
        if (clicks > 0 && GetElapsedTime(lastClick).TotalMilliseconds < ClickSettleMs)
            return ForeverClickResult.Pointing;
        if (clicks >= MaxClicks)
        {
            reason = $"{clicks} clicks on {target.Name} had no effect";
            return ForeverClickResult.Failed;
        }
        if (GetElapsedTime(began).TotalMilliseconds > BoundMs)
        {
            reason = $"{target.Name} was not under the cursor in time";
            return ForeverClickResult.Failed;
        }
        // Follow the thing to where this frame shows it; click once the game
        // reports it under the cursor.
        if (target.Where(bars) is not { } point)
            return ForeverClickResult.Pointing;
        if (pointer.MoveToward(point.X, point.Y) && target.IsHovered(bars))
        {
            pointer.Click();
            clicks++;
            lastClick = GetTimestamp();
        }
        return ForeverClickResult.Pointing;
    }
}
