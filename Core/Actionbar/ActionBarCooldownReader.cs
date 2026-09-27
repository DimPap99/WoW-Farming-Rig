using System;

using static Core.ActionBar;
using static System.Diagnostics.Stopwatch;
using static System.Math;

namespace Core;

public sealed class ActionBarCooldownReader : IReader
{
    // Set only for the Forever client. The provider rejects stale captures.
    public Func<VisualBarSample?>? ForeverSampleProvider { get; set; }

    public bool IsForeverReady(KeyAction action, SpellBookReader book)
    {
        return ForeverSampleProvider?.Invoke() is { } sample &&
            IsForeverReady(sample, action, book);
    }

    // Use this overload when a caller is about to send input. The spellbook,
    // action state, and cooldown must all be checked against that one capture.
    public static bool IsForeverReady(VisualBarSample sample,
        KeyAction action, SpellBookReader book)
    {
        if (action.Slot is < 1 or > VisualCooldownGridSnapshot.SlotCount ||
            sample.ActionBar is not
                { ReadableCount: VisualActionBarSnapshot.SlotCount } bar ||
            sample.ActionStates is not
                { ReadableCount: VisualActionBarSnapshot.SlotCount } states ||
            sample.Cooldowns is not
                { ReadableCount: VisualCooldownGridSnapshot.SlotCount } cooldowns ||
            sample.SpellBook is not { Count: > 0 } visualBook ||
            visualBook.ReadableCount != visualBook.Count ||
            bar.GetSlot(action.Slot) is not
                { Kind: VisualActionKind.Spell, Id: > 0 } placed ||
            (action.SpellId > 0 && action.SpellId != placed.Id) ||
            !HasExactVisualSpell(visualBook, placed.Id) ||
            !book.IsInitialized || !book.AllRanksReceived ||
            !book.HasExact(placed.Id) ||
            !book.TryGetValue(placed.Id, out var spell) ||
            !string.Equals(spell.Name, action.Name,
                StringComparison.OrdinalIgnoreCase) ||
            states.GetSlot(action.Slot) is not { Usable: true, NoMana: false } ||
            cooldowns.GetSlot(action.Slot) is not { } fill)
            return false;

        // A pixel-width remnant is still a running timer. Require an empty
        // bar; no fraction-to-milliseconds estimate is made.
        return fill == 0;
    }

    private static bool HasExactVisualSpell(VisualSpellBookSnapshot book, int id)
    {
        for (int slot = 1; slot <= book.Count; slot++)
            if (book.GetSlot(slot) is
                { Source: VisualSpellIdSource.SpellId, Id: var learnedId } &&
                learnedId == id)
                return true;
        return false;
    }

    private readonly struct Data
    {
        private readonly float durationSec;
        private readonly long start;

        public long End => start + (long)(durationSec * TimeSpan.TicksPerSecond);

        public Data(float durationSec, long start)
        {
            this.durationSec = durationSec;
            this.start = start;
        }
    }

    private const float FRACTION_PART = 10f;

    private const int cActionbarNum = 37;

    private readonly Data[] data;

    public ActionBarCooldownReader()
    {
        data = new Data[CELL_COUNT * BIT_PER_CELL];
        Reset();
    }

    public void Update(IAddonDataProvider reader)
    {
        int value = reader.GetInt(cActionbarNum);
        if (value == 0 || value < ACTION_SLOT_MUL)
            return;

        int slotIdx = (value / ACTION_SLOT_MUL) - 1;
        float durationSec = value % ACTION_SLOT_MUL / FRACTION_PART;

        data[slotIdx] = new(durationSec, GetTimestamp());
    }

    public void Reset()
    {
        var span = data.AsSpan();
        span.Fill(new(0, GetTimestamp()));
    }

    public int Get(KeyAction keyAction)
    {
        if (ForeverSampleProvider is not null)
            throw new NotSupportedException(
                "Forever cooldowns provide readiness only; CD_ millisecond comparisons require a verified timer source.");

        int index = keyAction.SlotIndex;

        ref readonly Data d = ref data[index];

        return Max((int)((d.End - GetTimestamp()) / TimeSpan.TicksPerMillisecond), 0);
    }
}
