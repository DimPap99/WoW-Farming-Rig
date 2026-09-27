using System;
using System.Collections.Specialized;
using System.Numerics;

using static Core.ActionBar;

namespace Core;

public interface IActionBarBits
{
    void Update(IAddonDataProvider reader);
    bool Is(KeyAction keyAction);

    bool Any { get; }
    int Count { get; }
}

public interface ICurrentAction : IActionBarBits { }

public interface IUsableAction : IActionBarBits { }

public sealed class ActionBarBits<T> : IActionBarBits, IReader
{
    public static Func<VisualActionStateSnapshot?>? ForeverStatesProvider { get; set; }
    private readonly int[] cells;

    private readonly BitVector32[] bits;

    public ActionBarBits(params ReadOnlySpan<int> cells)
    {
        this.cells = cells.ToArray();
        bits = new BitVector32[cells.Length];
    }

    public void Update(IAddonDataProvider reader)
    {
        Span<BitVector32> span = bits;
        for (int i = 0; i < span.Length; i++)
        {
            span[i] = new(reader.GetInt(cells[i]));
        }
    }

    // https://wowwiki-archive.fandom.com/wiki/ActionSlot
    public bool Is(KeyAction keyAction)
    {
        if (keyAction.Slot == 0) return false;

        int index = keyAction.SlotIndex;
        if (typeof(T) == typeof(IUsableAction) &&
            ForeverStatesProvider is not null)
            return IsSlotUsable(index + 1);

        return bits
            [index / BIT_PER_CELL]
            [Mask.M[index % BIT_PER_CELL]];
    }

    public bool IsSlotUsable(int slot) =>
        typeof(T) == typeof(IUsableAction) &&
        slot is >= 1 and <= VisualActionBarSnapshot.SlotCount &&
        ForeverStatesProvider?.Invoke()?.GetSlot(slot)?.Usable == true;

    public bool Any
    {
        get
        {
            if (typeof(T) == typeof(IUsableAction) &&
                ForeverStatesProvider is { } provider)
            {
                VisualActionStateSnapshot? states = provider();
                if (states is null)
                    return false;
                for (int slot = 1; slot <= VisualActionBarSnapshot.SlotCount; slot++)
                    if (states.GetSlot(slot)?.Usable == true)
                        return true;
                return false;
            }
            ReadOnlySpan<BitVector32> span = bits;
            BitVector32 zero = new();
            return span.IndexOfAnyExcept(zero) >= 0;
        }
    }

    public int Count
    {
        get
        {
            if (typeof(T) == typeof(IUsableAction) &&
                ForeverStatesProvider is { } provider)
            {
                VisualActionStateSnapshot? states = provider();
                int usable = 0;
                if (states is not null)
                    for (int slot = 1; slot <= VisualActionBarSnapshot.SlotCount; slot++)
                        if (states.GetSlot(slot)?.Usable == true)
                            usable++;
                return usable;
            }
            ReadOnlySpan<BitVector32> span = bits;
            int count = 0;
            foreach (BitVector32 b in span)
            {
                count += BitOperations.PopCount((nuint)b.Data);
            }
            return count;
        }
    }
}
