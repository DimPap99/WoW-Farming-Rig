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
        if (ForeverSampleProvider is not { } provider ||
            action.Slot is < 1 or > VisualCooldownGridSnapshot.SlotCount ||
            provider() is not { } sample ||
            sample.ActionBar is not
                { ReadableCount: VisualActionBarSnapshot.SlotCount } bar ||
            sample.ActionStates is not
                { ReadableCount: VisualActionBarSnapshot.SlotCount } states ||
            sample.Cooldowns is not
                { ReadableCount: VisualCooldownGridSnapshot.SlotCount } cooldowns ||
            bar.GetSlot(action.Slot) is not
                { Kind: VisualActionKind.Spell, Id: > 0 } placed ||
            (action.SpellId > 0 && action.SpellId != placed.Id) ||
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
