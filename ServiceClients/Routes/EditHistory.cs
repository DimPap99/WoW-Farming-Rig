using System.Collections.Generic;

namespace ServiceClients.Routes;

/// <summary>
/// Undo and redo over whole values: each change records the value it replaces. Values are kept as they
/// are, so they must not change afterwards (records and arrays nobody writes to). The oldest go first
/// beyond <paramref name="capacity"/>.
/// </summary>
public sealed class EditHistory<T>(int capacity = 200)
{
    private readonly LinkedList<T> undo = [];
    private readonly Stack<T> redo = [];

    public bool CanUndo => undo.Count > 0;

    public bool CanRedo => redo.Count > 0;

    /// <summary>A change is about to replace <paramref name="before"/>; what was undone can no longer be redone.</summary>
    public void Record(T before)
    {
        undo.AddLast(before);
        if (undo.Count > capacity)
        {
            undo.RemoveFirst();
        }

        redo.Clear();
    }

    /// <summary>The value before the last change, with <paramref name="current"/> kept to redo; false when there is none.</summary>
    public bool TryUndo(T current, out T previous)
    {
        if (undo.Last is not LinkedListNode<T> last)
        {
            previous = current;
            return false;
        }

        undo.RemoveLast();
        redo.Push(current);
        previous = last.Value;
        return true;
    }

    /// <summary>The value the last undo went back from, with <paramref name="current"/> kept to undo again; false when there is none.</summary>
    public bool TryRedo(T current, out T next)
    {
        if (!redo.TryPop(out T? popped))
        {
            next = current;
            return false;
        }

        undo.AddLast(current);
        next = popped!;
        return true;
    }

    public void Clear()
    {
        undo.Clear();
        redo.Clear();
    }
}
