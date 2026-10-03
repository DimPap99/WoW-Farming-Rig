using Core;

using SixLabors.ImageSharp.PixelFormats;

using System;
using System.Diagnostics;

namespace ForeverVisualReader;

/// <summary>One roster slot as ForeverNameplates.lua and ForeverVisual.lua encode it.</summary>
internal readonly record struct PlateSpec(
    int Incarnation, int Hash1 = 0, int Hash2 = 0, int Level = 7,
    int Attackable = 2, int Dead = 1, int IsPlayer = 1, int TapDenied = 1, int InCombat = 1,
    int IsTarget = 1, int IsMouseover = 1, int BehindCamera = 1, int TargetsPlayer = 1,
    int Reaction = 2, int Threat = 1, int Alpha = 255, int Ranges = 0, int Close = 1);

/// <summary>The roster's pixel values, written the way the add-on writes them.</summary>
internal static class RosterCodec
{
    /// <summary>Header values: counts, settings, version and checksum.</summary>
    public static int[] Header(int plates, int attackable, int friendly,
        int showEnemies = 2, int showAll = 2, int maxDistance = 45, int version = VisualNameplateReader.ProtocolVersion)
    {
        int counts = plates + attackable * 256 + friendly * 65536;
        int settings = showEnemies + showAll * 4 + maxDistance * 16;
        return [counts, settings, version, (0xA5C211 - counts - settings - version) & 0xFFFFFF];
    }

    /// <summary>A slot row; a target row's checksum includes the target's hashes.</summary>
    public static int[] Row(int slot, PlateSpec? plate, VisualTargetIdentity? target = null)
    {
        int[] v = new int[7];
        if (plate is { } p)
        {
            v[0] = p.Incarnation;
            v[1] = p.Hash1;
            v[2] = p.Hash2;
            v[3] = p.Level + p.Attackable * 256 + p.Dead * 1024 + p.IsPlayer * 4096 +
                p.TapDenied * 16384 + p.InCombat * 65536 + p.IsTarget * 262144 +
                p.IsMouseover * 1048576 + p.BehindCamera * 4194304;
            v[4] = p.TargetsPlayer + p.Reaction * 4 + p.Threat * 64 + p.Alpha * 512 + p.Close * 131072;
            v[5] = p.Ranges;
        }
        int checksum = 0xA5C311 - slot;
        for (int i = 0; i < 6; i++)
            checksum -= v[i];
        if (plate is { IsTarget: 2 } && target is { } t)
            checksum -= t.GuidHash1 + t.GuidHash2;
        v[6] = checksum & 0xFFFFFF;
        return v;
    }

    /// <summary>Two-bit range codes for main-bar slots 1 to 12: 1 in range, 2 out.</summary>
    public static int Ranges(params (int Slot, bool InRange)[] slots)
    {
        int packed = 0;
        foreach ((int slot, bool inRange) in slots)
            packed |= (inRange ? 1 : 2) << ((slot - 1) * 2);
        return packed;
    }
}

/// <summary>A minimal /forevervisual panel and nameplate markers drawn into a synthetic frame.</summary>
internal sealed class SyntheticPanel(int width, int height, double scale, int left = 16, int top = 70)
{
    public Bgra32[] Pixels { get; } = new Bgra32[width * height];
    public int Width => width;
    public int Height => height;

    /// <summary>The calibration rows TryRead needs: white full, yellow at half.</summary>
    public SyntheticPanel WithCalibration()
    {
        Bar(3, 0.5, new Bgra32(255, 255, 0));
        Bar(4, 1.0, new Bgra32(255, 255, 255));
        return this;
    }

    public void Bar(int row, double fraction, Bgra32 color)
    {
        int barWidth = (int)Math.Round(320 * scale);
        int rowStep = (int)Math.Round(17 * scale);
        int barHeight = (int)Math.Round(10 * scale);
        int fill = (int)Math.Round(barWidth * fraction);
        int yStart = top + row * rowStep;
        for (int y = yStart; y < yStart + barHeight; y++)
            for (int x = left; x < left + fill; x++)
                Pixels[y * width + x] = color;
    }

    /// <summary>Value cells from <paramref name="firstIndex"/> on, 20 units apart, on a frame row.</summary>
    public void Cells(int firstIndex, int frameTop, int[] values)
    {
        int side = (int)Math.Round(10 * scale);
        for (int i = 0; i < values.Length; i++)
        {
            int xStart = left + (int)Math.Round((firstIndex + i) * 20 * scale);
            int yStart = top + (int)Math.Round(frameTop * scale);
            Bgra32 color = new((byte)(values[i] >> 16), (byte)(values[i] >> 8), (byte)values[i]);
            for (int y = yStart; y < yStart + side; y++)
                for (int x = xStart; x < xStart + side; x++)
                    Pixels[y * width + x] = color;
        }
    }

    public void Header(int[] values) => Cells(17, 245, values);

    public void Row(int slot, int[] values) => Cells(17, 260 + (slot - 1) * 15, values);

    /// <summary>The checked target identity cells; a zero identity means no target.</summary>
    public void Target(VisualTargetIdentity identity)
    {
        int packedLevel = identity.Level * 2 + (identity.IsPlayer ? 1 : 0);
        Cells(3, 190, [identity.GuidHash1, identity.GuidHash2, packedLevel,
            (0xA58866 - identity.GuidHash1 - identity.GuidHash2 - packedLevel) & 0xFFFFFF]);
    }

    /// <summary>
    /// The attack-state cells: current and auto-repeat slot bits, then melee and
    /// target distance as tri-states (0 unknown, 1 false, 2 true); a known
    /// distance folds in <paramref name="target"/>'s hashes.
    /// </summary>
    public void Attack(int current, int autoRepeat, int melee, int close,
        VisualTargetIdentity target, int version = VisualAttackReader.ProtocolVersion)
    {
        int slots = current | (autoRepeat << 12);
        int state = melee + close * 4 + version * 16;
        int checksum = 0xA5E111 - slots - state;
        if (close != 0)
            checksum -= target.GuidHash1 + target.GuidHash2;
        Cells(17, 190, [slots, state, checksum & 0xFFFFFF]);
    }

    /// <summary>The slot-coded marker centred at <paramref name="x"/>, <paramref name="y"/> pixels.</summary>
    public void Marker(int x, int y, int slot)
    {
        int size = (int)Math.Round(28 * scale);
        int ring = (int)Math.Round(22 * scale);
        int core = Math.Max(3, (int)Math.Round(6 * scale));
        Square(x, y, size, new Bgra32(255, 0, 255));
        Square(x, y, ring, new Bgra32(0, 0, 0));
        Square(x, y, core, new Bgra32((byte)(16 + 32 * (slot - 1)), 255, 255));
    }

    private void Square(int cx, int cy, int side, Bgra32 color)
    {
        for (int y = cy - side / 2; y < cy - side / 2 + side; y++)
            for (int x = cx - side / 2; x < cx - side / 2 + side; x++)
                Pixels[y * width + x] = color;
    }
}

/// <summary>Synthetic checks of the nameplate roster and its markers at three UI scales.</summary>
internal static class NameplateSelfTest
{
    public static bool Run()
    {
        bool ok = true;
        void Check(bool passed, string name)
        {
            if (!passed)
            {
                Console.Error.WriteLine($"Nameplate self-test failed: {name}");
                ok = false;
            }
        }

        VisualTargetIdentity target = new(123456, 654321, 7, false);
        foreach (double scale in new[] { 0.75, 1.0, 1.25 })
        {
            const int width = 1000, height = 800;
            string at = $" at {scale:P0}";

            // Slot 1: the target, in melee and Serpent Sting range. Slot 2: a
            // far, occluded mob the player has never fought. Slot 3: behind
            // the camera, no marker. Slot 4: an attacker with a protected GUID.
            // Slot 5 is empty; slot 6 is corrupt; slots 7 and 8 are empty.
            PlateSpec targetPlate = new(11, target.GuidHash1, target.GuidHash2, IsTarget: 2,
                Ranges: RosterCodec.Ranges((1, true), (3, true)), Close: 2);
            PlateSpec farPlate = new(12, 777, 888, Level: 6, Alpha: 1 + (int)Math.Round(0.24 * 254),
                Ranges: RosterCodec.Ranges((1, true), (3, false)));
            PlateSpec behindPlate = new(13, 999, 1111, BehindCamera: 2);
            PlateSpec attacker = new(14, InCombat: 2, TargetsPlayer: 2, Threat: 5, Reaction: 1, Close: 0);

            SyntheticPanel panel = new SyntheticPanel(width, height, scale).WithCalibration();
            panel.Header(RosterCodec.Header(plates: 6, attackable: 5, friendly: 1));
            panel.Row(1, RosterCodec.Row(1, targetPlate, target));
            panel.Row(2, RosterCodec.Row(2, farPlate));
            panel.Row(3, RosterCodec.Row(3, behindPlate));
            panel.Row(4, RosterCodec.Row(4, attacker));
            panel.Row(5, RosterCodec.Row(5, null));
            int[] corrupt = RosterCodec.Row(6, new(15));
            corrupt[3] ^= 1;
            panel.Row(6, corrupt);
            panel.Row(7, RosterCodec.Row(7, null));
            panel.Row(8, RosterCodec.Row(8, null));
            panel.Marker(700, 500, 1);
            panel.Marker(560, 300, 2);
            panel.Marker(820, 640, 4);

            // The roster's target row is tied to the target identity read
            // from the same frame; a frame without one refuses that row.
            panel.Target(target);
            Check(VisualBarReader.TryRead(panel.Pixels, width, height, out VisualBarSample withTarget),
                "panel read" + at);
            VisualNameplateRoster? roster = withTarget.Nameplates;
            Check(roster is { PlateCount: 6, AttackableCount: 5, FriendlyPlayerCount: 1,
                ShowEnemies: true, ShowAll: true, MaxDistance: 45, PlatesShown: true }, "header" + at);
            Check(roster?.ReadableCount == 7, $"seven readable rows{at}: {roster?.ReadableCount}");
            Check(roster?.Target is { Slot: 1, Incarnation: 11 } t1 && t1.Is(target), "target slot" + at);
            Check(roster?.GetSlot(1) is { Close: true } s1 && s1.Ranges.InRange(1) == true &&
                s1.Ranges.InRange(3) == true && s1.Ranges.InRange(2) is null &&
                s1.Occluded is null && !s1.Attacks, "target ranges, within duel distance" + at);
            Check(roster?.GetSlot(2) is { Occluded: true, Level: 6, Reaction: 2, OnThreatTable: false, Close: false } s2 &&
                s2.Ranges.InRange(3) == false && s2.Alpha is > 0.23 and < 0.25, "occluded far plate" + at);
            Check(roster?.GetSlot(3) is { BehindCamera: true, Screen: null }, "behind camera" + at);
            Check(roster?.GetSlot(4) is { HasIdentity: false, Attacks: true, ThreatStatus: 3, InCombat: true, Close: null },
                "attacker, distance unknown" + at);
            Check(roster?.GetSlot(5) is { IsEmpty: true }, "empty slot" + at);
            Check(roster?.GetSlot(6) is null, "corrupt row refused" + at);

            // Markers: each slot's own, at its own place.
            double tolerance = 2.0 / width;
            Check(roster?.GetSlot(1)?.Screen is { } m1 &&
                Math.Abs(m1.X - 700.0 / width) < tolerance && Math.Abs(m1.Y - (1 - 500.0 / height)) < 2.0 / height,
                $"slot 1 marker{at}: {roster?.GetSlot(1)?.Screen}");
            Check(roster?.GetSlot(2)?.Screen is { } m2 && Math.Abs(m2.X - 560.0 / width) < tolerance,
                $"slot 2 marker{at}: {roster?.GetSlot(2)?.Screen}");
            Check(roster?.GetSlot(4)?.Screen is { } m4 && Math.Abs(m4.X - 820.0 / width) < tolerance,
                $"slot 4 marker{at}: {roster?.GetSlot(4)?.Screen}");
            Check(withTarget.TargetScreenPosition == roster?.GetSlot(1)?.Screen, "target screen from its slot" + at);

            panel.Target(new(0, 0, 0, false));
            VisualBarSample withoutTarget = Read(panel);
            Check(withoutTarget.Nameplates?.GetSlot(1) is null && withoutTarget.Nameplates?.Target is null &&
                withoutTarget.TargetScreenPosition is null, "target row needs the frame's target identity" + at);
            panel.Target(target with { GuidHash1 = 1 });
            Check(Read(panel).Nameplates?.GetSlot(1) is null, "target row of another target refused" + at);
            panel.Target(target);

            // Two markers wearing one slot's colours are not trusted.
            panel.Marker(300, 200, 2);
            Check(Read(panel).Nameplates?.GetSlot(2)?.Screen is null, "duplicate slot marker refused" + at);

            // The attack state: slot 1 pending its swing, slot 2 repeating,
            // melee off, the target within duel distance.
            panel.Attack(current: 1, autoRepeat: 1 << 1, melee: 1, close: 2, target);
            Check(Read(panel).Attack is { Melee: false, TargetClose: true, AutoRepeat: true } a1 &&
                a1.IsCurrent(1) && !a1.IsCurrent(2) && a1.IsAutoRepeating(2) && !a1.IsAutoRepeating(1) &&
                !a1.IsCurrent(13), $"attack state{at}: {Read(panel).Attack}");
            panel.Target(target with { GuidHash1 = 1 });
            Check(Read(panel).Attack is null, "attack state's distance of another target refused" + at);
            panel.Target(new(0, 0, 0, false));
            Check(Read(panel).Attack is null, "attack state's distance needs the frame's target identity" + at);
            panel.Attack(current: 0, autoRepeat: 0, melee: 2, close: 0, new(0, 0, 0, false));
            Check(Read(panel).Attack is { Melee: true, TargetClose: null, AutoRepeat: false },
                "attack state without a target: melee on, distance unknown" + at);
            panel.Attack(current: 0, autoRepeat: 0, melee: 2, close: 0, new(0, 0, 0, false), version: 2);
            Check(Read(panel).Attack is null, "other attack-state version refused" + at);
            panel.Target(target);
            panel.Attack(current: 1, autoRepeat: 1 << 1, melee: 1, close: 2, target);

            // An old layout is refused rather than misread.
            panel.Header(RosterCodec.Header(6, 5, 1, version: 1));
            Check(Read(panel).Nameplates is null, "other roster version refused" + at);
        }

        // The full-viewport scan at 1920x1080, as the tracker runs it each frame.
        SyntheticPanel full = new SyntheticPanel(1920, 1080, 1.0).WithCalibration();
        full.Header(RosterCodec.Header(1, 1, 0));
        full.Row(1, RosterCodec.Row(1, new PlateSpec(1)));
        full.Marker(1500, 900, 1);
        for (int i = 0; i < 3; i++)
            VisualMarkerLocator.Locate(full.Pixels, full.Width, full.Height, 1.0);
        Stopwatch watch = Stopwatch.StartNew();
        const int scans = 20;
        VisualScreenPointSample?[] located = [];
        for (int i = 0; i < scans; i++)
            located = VisualMarkerLocator.Locate(full.Pixels, full.Width, full.Height, 1.0);
        double ms = watch.Elapsed.TotalMilliseconds / scans;
        Check(located[0] is { } point && Math.Abs(point.X - 1500.0 / 1920) < 0.002, "1080p marker");
        Console.WriteLine($"Nameplate marker scan: {ms:F2} ms per 1920x1080 frame.");
        return ok;
    }

    private static VisualBarSample Read(SyntheticPanel panel) =>
        VisualBarReader.TryRead(panel.Pixels, panel.Width, panel.Height, out VisualBarSample bars)
            ? bars : default;
}
