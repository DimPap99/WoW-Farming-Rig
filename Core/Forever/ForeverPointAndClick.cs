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
/// cursor goes onto the thing in one swift eased stroke, the click comes the
/// moment the game reports the thing under the cursor, even while it moves,
/// and it counts once the game shows its effect. A moving thing is aimed
/// ahead, where the stroke will find it. A click without effect is retried once.
/// </summary>
public sealed class ForeverPointAndClick(IMousePointer pointer)
{
    // A click the game has not answered within this long is retried.
    private const int ClickSettleMs = 1000;
    private const int MaxClicks = 2;
    // Aiming and clicking gets this long.
    private const int BoundMs = 5000;
    // The game's hover report is trusted once the cursor has rested this
    // long: the add-on samples it ten times a second, so an earlier report
    // may describe the cursor mid-stroke.
    private const int HoverSettleMs = 120;
    // A stroke aims this far ahead along the thing's screen velocity.
    private const double LeadSeconds = 0.2;

    private IForeverClickTarget? target;
    private int clicks;
    private long began;
    private long lastClick;
    // When the cursor last stopped moving; 0 before the first stroke.
    private long restingSince;
    private VisualScreenPointSample? lastPoint;
    private long lastPointAt;
    private double velocityX;
    private double velocityY;

    public void Begin(IForeverClickTarget clickTarget)
    {
        target = clickTarget;
        clicks = 0;
        lastClick = 0;
        restingSince = 0;
        lastPoint = null;
        velocityX = velocityY = 0;
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
        bool hovered = target.IsHovered(bars);
        if (clicks >= MaxClicks)
        {
            reason = $"{clicks} clicks on {target.Name} had no effect; " +
                (hovered ? "the game still reports it under the cursor" : "it is no longer under the cursor");
            return ForeverClickResult.Failed;
        }
        if (GetElapsedTime(began).TotalMilliseconds > BoundMs)
        {
            reason = $"{target.Name} was not under the cursor in time" +
                (target.Where(bars) is null ? "; it was out of sight" : string.Empty);
            return ForeverClickResult.Failed;
        }
        if (target.Where(bars) is not { } point)
            return ForeverClickResult.Pointing;
        Track(point);

        // Click the first time the game reports the thing under the resting
        // cursor, moving or not; otherwise follow it to where it is going.
        if (restingSince != 0 && hovered &&
            GetElapsedTime(restingSince).TotalMilliseconds >= HoverSettleMs)
        {
            pointer.Click();
            clicks++;
            lastClick = GetTimestamp();
            return ForeverClickResult.Pointing;
        }
        if (restingSince != 0 && GetElapsedTime(restingSince).TotalMilliseconds < HoverSettleMs)
            return ForeverClickResult.Pointing;
        if (!pointer.MoveToward(point.X + velocityX * LeadSeconds, point.Y + velocityY * LeadSeconds))
            restingSince = GetTimestamp();
        else if (restingSince == 0)
            restingSince = GetTimestamp();
        return ForeverClickResult.Pointing;
    }

    // The thing's screen velocity, smoothed over consecutive sightings.
    private void Track(VisualScreenPointSample point)
    {
        long now = GetTimestamp();
        if (lastPoint is { } previous)
        {
            double seconds = GetElapsedTime(lastPointAt).TotalSeconds;
            if (seconds is > 0.01 and < 0.5)
            {
                velocityX = 0.5 * velocityX + 0.5 * (point.X - previous.X) / seconds;
                velocityY = 0.5 * velocityY + 0.5 * (point.Y - previous.Y) / seconds;
            }
        }
        lastPoint = point;
        lastPointAt = now;
    }
}
