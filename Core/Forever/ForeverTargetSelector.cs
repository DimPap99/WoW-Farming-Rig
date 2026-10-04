using Game;

using Microsoft.Extensions.Logging.Abstractions;

using SharedLib;

using System;

namespace Core.Forever;

/// <summary>
/// Makes the game target the unit the runtime chose, the way a person does:
/// a <see cref="ForeverPointAndClick"/> on the unit's nameplate, done once the
/// same plate reports itself the target. Also clears the target.
/// </summary>
public sealed class ForeverTargetSelector
{
    private readonly ConfigurableInput input;
    private readonly ForeverPointAndClick click;
    private readonly KeyAction? profileClearTarget;

    public ForeverTargetSelector(ConfigurableInput input, IMousePointer pointer, ClassConfiguration config)
    {
        this.input = input;
        click = new(pointer);
        profileClearTarget = ProfileClearTarget(config);
    }

    /// <summary>Starts selecting <paramref name="candidate"/>; <see cref="Select"/> follows it.</summary>
    public void BeginSelect(VisualNameplate candidate) =>
        click.Begin(new NameplateClickTarget(candidate.Slot, candidate.Incarnation));

    /// <summary>One step of the selection begun with <see cref="BeginSelect"/>, on a fresh frame.</summary>
    public ForeverClickResult Select(VisualBarSample bars, out string reason) =>
        click.Update(bars, out reason);

    /// <summary>A roster slot's unit while the slot keeps it: clicked on its plate until it is the target.</summary>
    private sealed class NameplateClickTarget(int slot, int incarnation) : IForeverClickTarget
    {
        public string Name => "the candidate's nameplate";

        public bool IsGone(VisualBarSample bars) => Plate(bars) is null;

        public VisualScreenPointSample? Where(VisualBarSample bars) => Plate(bars)?.Screen;

        public bool IsHovered(VisualBarSample bars) => Plate(bars)?.IsMouseover == true;

        public bool IsDone(VisualBarSample bars) => Plate(bars)?.IsTarget == true;

        private VisualNameplate? Plate(VisualBarSample bars) =>
            bars.Nameplates?.GetSlot(slot) is { IsEmpty: false } plate && plate.Incarnation == incarnation
                ? plate : null;
    }

    /// <summary>
    /// Clears the target the way the player set the client up, and only while
    /// this frame shows one: with nothing selected Escape opens the game menu.
    /// Callers wait for the flags to follow before pressing again.
    /// </summary>
    public bool ClearTarget(VisualBarSample bars)
    {
        if (bars.TargetFlags is not { Present: true })
            return false;
        input.PressRandom(ClearTargetKey(bars));
        return true;
    }

    /// <summary>
    /// The profile's <c>"ClearTarget": { "Key": ... }</c> when the client binds
    /// that key to a macro on the main bar, then upstream's clear-target
    /// binding when the client has one, then Escape.
    /// </summary>
    private KeyAction ClearTargetKey(VisualBarSample bars)
    {
        if (profileClearTarget is { } profile &&
            ForeverBindings.MainBarSlotBoundTo(bars, profile.ConsoleKey, profile.Modifier) is int macroSlot &&
            bars.ActionBar?.GetSlot(macroSlot) is { Kind: VisualActionKind.Macro })
            return profile;
        if (ForeverBindings.ClearTarget.TryGetKeyAction(bars, ClearTargetName, out KeyAction bound))
            return bound;
        return ForeverBinding.ToKeyAction(ClearTargetName, ConsoleKey.Escape, ModifierKey.None);
    }

    private const string ClearTargetName = "Forever Clear Target";

    // Upstream resolves ClearTarget from its binding default before its Key,
    // so the profile's Key string is read here on its own.
    private static KeyAction? ProfileClearTarget(ClassConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.ClearTarget.Key))
            return null;
        KeyAction key = new()
        {
            Name = ClearTargetName,
            Key = config.ClearTarget.Key,
            BaseAction = true,
            PressDuration = ForeverBinding.PressMs
        };
        return KeyReader.ResolveFromKeyString(NullLogger.Instance, key) ? key : null;
    }
}
