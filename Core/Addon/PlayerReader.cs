using Core.Addon;
using Core.Database;

using SharedLib;

using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Numerics;

namespace Core;

public sealed partial class PlayerReader : IMouseOverReader, IReader
{
    private readonly IAddonDataProvider reader;
    private readonly WorldMapAreaDB worldMapAreaDB;
    private readonly AreaDB areaDb;
    private readonly AddonBits bits;
    private readonly ForeverVisualBarTracker? foreverVisualBars;

    public PlayerReader(
        IAddonDataProvider reader,
        WorldMapAreaDB mapAreaDB,
        AreaDB areaDb,
        AddonBits addonBits,
        SpellInRange spellInRange,
        Stance stance,
        ForeverVisualBarTracker? foreverVisualBars = null)
    {
        this.worldMapAreaDB = mapAreaDB;
        this.areaDb = areaDb;
        this.reader = reader;

        bits = addonBits;
        this.foreverVisualBars = foreverVisualBars;
        SpellInRange = spellInRange;
        Stance = stance;

        // TODO: inject! value type tho
        CustomTrigger1 = new(IsForever ? 0 : reader.GetInt(74));
    }

    public WorldMapArea WorldMapArea { get; private set; }

    public Vector3 MapPos => new(MapX, MapY, WorldPosZ);
    public Vector3 MapPosNoZ => new(MapX, MapY, 0);
    public Vector3 _MapPosNoZ() => MapPosNoZ;
    public Vector3 WorldPos
    {
        get
        {
            if (!IsForever)
                return worldMapAreaDB.ToWorld_FlipXY(UIMapId.Value, MapPos);

            VisualPositionSample position = RequiredForeverPosition();
            if (!worldMapAreaDB.TryGet(position.UIMapId, out WorldMapArea area))
                throw new InvalidOperationException(
                    $"Forever UI map {position.UIMapId} is not in the map table.");

            return WorldMapAreaDB.ToWorld_FlipXY(
                new Vector3((float)(position.MapX * 100),
                    (float)(position.MapY * 100), WorldPosZ), area);
        }
    }

    public float WorldPosZ { get; set; } // MapZ not exists. Alias for WorldLoc.Z

    public float MapX => IsForever
        ? (float)(RequiredForeverPosition().MapX * 100)
        : reader.GetFixed24(1);
    public float MapY => IsForever
        ? (float)(RequiredForeverPosition().MapY * 100)
        : reader.GetFixed24(2);

    public Vector3 TargetMapPos
    {
        get
        {
            if (!bits.Target())
            {
                return Vector3.Zero;
            }

            float targetDistance = (MaxRange() + MinRange()) / 2;

            return PointEstimator.GetMapPos(WorldMapArea, WorldPos, Direction, targetDistance);
        }
    }

    public float Direction => IsForever
        ? (float)RequiredForeverPosition().Direction
        : reader.GetFixed20(3);

    public float _Direction() => Direction;

    public RecordInt UIMapId { get; } = new(4);

    public int MapId { get; private set; }

    public RecordInt Level { get; } = new(5);

    public Vector3 CorpseMapPos => new(CorpseMapX, CorpseMapY, 0);
    public float CorpseMapX => reader.GetFixed24(6);
    public float CorpseMapY => reader.GetFixed24(7);

    private int ClassicAbsoluteVital(int cell)
    {
        if (IsForever)
            throw new NotSupportedException(
                "Forever absolute vitals are not ported; legacy pixel cells are unavailable.");

        return reader.GetInt(cell);
    }

    public int HealthMax() => IsForever
        ? RequiredForeverVitals().PlayerHealthMax ?? throw new InvalidOperationException(
            "Forever player maximum health is unavailable or unreadable.")
        : ClassicAbsoluteVital(10);

    // Read-only Forever percentage source. The sample expires with capture;
    // callers that can tolerate missing data should use this Try method.
    public bool TryGetForeverVisualVitals(out VisualBarSample bars)
    {
        if (foreverVisualBars is not null)
            return foreverVisualBars.TryGetLatest(out bars);

        bars = default;
        return false;
    }

    public bool TryGetForeverPosition(out VisualPositionSample position)
    {
        if (TryGetForeverVisualVitals(out VisualBarSample bars) &&
            bars.Position is { } latest)
        {
            position = latest;
            return true;
        }

        position = default;
        return false;
    }

    public bool TryGetForeverWorldPosition(out VisualWorldPositionSample position)
    {
        if (TryGetForeverVisualVitals(out VisualBarSample bars) &&
            bars.WorldPosition is { } latest)
        {
            position = latest;
            return true;
        }

        position = default;
        return false;
    }

    public bool TryGetForeverWatchedAction(out VisualActionSample action)
    {
        if (TryGetForeverVisualVitals(out VisualBarSample bars) &&
            bars.WatchedAction is { } latest)
        {
            action = latest;
            return true;
        }

        action = default;
        return false;
    }

    public bool TryGetForeverActionBar(out VisualActionBarSnapshot actionBar)
    {
        if (TryGetForeverVisualVitals(out VisualBarSample bars) &&
            bars.ActionBar is { } latest)
        {
            actionBar = latest;
            return true;
        }

        actionBar = null!;
        return false;
    }

    public bool TryGetForeverActionStates(out VisualActionStateSnapshot states)
    {
        if (TryGetForeverVisualVitals(out VisualBarSample bars) &&
            bars.ActionStates is { ReadableCount: VisualActionBarSnapshot.SlotCount } latest)
        {
            states = latest;
            return true;
        }

        states = null!;
        return false;
    }

    public bool TryGetForeverCooldowns(out VisualCooldownGridSnapshot cooldowns)
    {
        if (TryGetForeverVisualVitals(out VisualBarSample bars) &&
            bars.Cooldowns is { ReadableCount: VisualCooldownGridSnapshot.SlotCount } latest)
        {
            cooldowns = latest;
            return true;
        }

        cooldowns = null!;
        return false;
    }

    public bool TryGetForeverBags(out VisualBagSnapshot bags)
    {
        if (TryGetForeverVisualVitals(out VisualBarSample bars) &&
            bars.Bags is { IsComplete: true } latest)
        {
            bags = latest;
            return true;
        }

        bags = null!;
        return false;
    }

    public bool TryGetForeverSpellBook(out VisualSpellBookSnapshot spellBook)
    {
        if (TryGetForeverVisualVitals(out VisualBarSample bars) &&
            bars.SpellBook is { } latest)
        {
            spellBook = latest;
            return true;
        }

        spellBook = null!;
        return false;
    }

    public bool TryGetForeverBindings(out VisualBindingSnapshot bindings)
    {
        if (TryGetForeverVisualVitals(out VisualBarSample bars) &&
            bars.Bindings is { } latest)
        {
            bindings = latest;
            return true;
        }

        bindings = null!;
        return false;
    }

    public bool TryGetForeverCastEvent(out VisualCastEvent castEvent)
    {
        if (TryGetForeverVisualVitals(out VisualBarSample bars) &&
            bars.LastCastEvent is { } latest)
        {
            castEvent = latest;
            return true;
        }

        castEvent = default;
        return false;
    }

    public bool IsForeverVisual => foreverVisualBars?.IsEnabled == true;
    private bool IsForever => IsForeverVisual;

    private VisualBarSample RequiredForeverVitals()
    {
        if (TryGetForeverVisualVitals(out VisualBarSample bars))
            return bars;

        throw new InvalidOperationException(
            "Forever visual vitals are unavailable or stale; refusing legacy pixel data.");
    }

    private VisualPositionSample RequiredForeverPosition()
    {
        if (TryGetForeverPosition(out VisualPositionSample position))
            return position;

        throw new InvalidOperationException(
            "Forever visual position is unavailable or stale; refusing legacy pixel data.");
    }

    private static int VisualPercent(double fraction) =>
        (int)Math.Round(Math.Clamp(fraction, 0, 1) * 100, MidpointRounding.AwayFromZero);

    public int HealthCurrent() => ClassicAbsoluteVital(11);
    public int HealthPercent() => IsForever
        ? VisualPercent(RequiredForeverVitals().PlayerHealth)
        : (1 + HealthCurrent()) * 100 / (1 + HealthMax());

    public int PTMax() => IsForever
        ? RequiredForeverVitals().PlayerPowerMax ?? throw new InvalidOperationException(
            "Forever player maximum power is unavailable or unreadable.")
        : ClassicAbsoluteVital(12); // Maximum amount of Power Type (dynamic)
    public int PTCurrent() => ClassicAbsoluteVital(13); // Current amount of Power Type (dynamic)
    public int PTPercentage() => IsForever
        ? VisualPercent(RequiredForeverVitals().PlayerPower)
        : PTCurrent() * 100 / PTMax(); // Power Type (dynamic) in terms of a percentage

    public int ManaMax() => IsForever
        ? RequiredForeverManaVitals().PlayerPowerMax ?? throw new InvalidOperationException(
            "Forever maximum mana is unavailable or unreadable.")
        : ClassicAbsoluteVital(14);
    public int ManaCurrent() => ClassicAbsoluteVital(15);
    public int ManaPercent() => IsForever
        ? VisualPercent(RequiredForeverManaVitals().PlayerPower)
        : (1 + ManaCurrent()) * 100 / (1 + ManaMax());

    private VisualBarSample RequiredForeverManaVitals()
    {
        VisualBarSample bars = RequiredForeverVitals();
        if (bars.PlayerPowerType != 0)
            throw new InvalidOperationException(
                "Forever mana is unavailable: the checked primary power type is not mana.");
        return bars;
    }

    public int MaxRune() => reader.GetInt(14);

    public int BloodRune() => reader.GetInt(15) / 100 % 10;
    public int FrostRune() => reader.GetInt(15) / 10 % 10;
    public int UnholyRune() => reader.GetInt(15) % 10;

    public int TargetMaxHealth() => ClassicAbsoluteVital(18);
    public int TargetHealth() => ClassicAbsoluteVital(19);
    public int TargetHealthPercent()
    {
        if (!IsForever)
            return (1 + TargetHealth()) * 100 / (1 + TargetMaxHealth());

        VisualBarSample bars = RequiredForeverVitals();
        if (bars.TargetPresent != true)
            throw new InvalidOperationException(
                "Forever target health is unavailable: no confirmed target is present.");

        return VisualPercent(bars.TargetHealth);
    }

    public int PetMaxHealth() => ClassicAbsoluteVital(38);
    public int PetHealth() => ClassicAbsoluteVital(39);
    public int PetHealthPercent() => (1 + PetHealth()) * 100 / (1 + PetMaxHealth());
    public bool PetAlive() => PetHealth() > 0;

    public SpellInRange SpellInRange { get; }
    public bool WithInPullRange() => SpellInRange.WithinPullRange(this, Class);
    public bool WithInCombatRange() => SpellInRange.WithinCombatRange(this, Class);
    public bool OutOfCombatRange() => !SpellInRange.WithinCombatRange(this, Class);

    // TargetLevel * 100 + TargetClass
    public int TargetLevel => reader.GetInt(43) / 100;
    public UnitClassification TargetClassification => (UnitClassification)(reader.GetInt(43) % 100);

    public bool TargetIsElite() => TargetClassification == UnitClassification.Elite;

    // Copper, split across two cells because one cannot hold a full purse.
    public RecordInt Money { get; } = new(44, 45, 1000000);

    // FACTION_ID * 1000000 + RACE_ID * 10000 + CLASS_ID * 100 + ClientVersion
    // On Forever an unreadable identity reads as None, like an empty Classic
    // cell, so BotController's startup loop keeps waiting instead of aborting.
    public UnitRace Race => IsForever
        ? (UnitRace)(ForeverIdentity()?.RaceId ?? 0)
        : (UnitRace)(reader.GetInt(46) / 10000 % 100);
    public UnitClass Class => IsForever
        ? (UnitClass)(ForeverIdentity()?.ClassId ?? 0)
        : (UnitClass)(reader.GetInt(46) / 100 % 100);
    public ClientVersion Version => IsForever
        ? ClientVersion.Forever
        : (ClientVersion)(reader.GetInt(46) % 100);

    private VisualPlayerIdentity? ForeverIdentity() =>
        TryGetForeverVisualVitals(out VisualBarSample bars) ? bars.PlayerIdentity : null;

    // Sent by the addon rather than derived from the race: a Pandaren is
    // Neutral until the Wandering Isle is finished, then becomes either side.
    public PlayerFaction Faction => IsForever
        ? ForeverFaction(Race)
        : (PlayerFaction)(reader.GetInt(46) / 1000000);

    // Forever's visual channel has no faction cell. The original eight races
    // have a fixed side; any other race is refused rather than guessed.
    private static PlayerFaction ForeverFaction(UnitRace race) => race switch
    {
        UnitRace.Human or UnitRace.Dwarf or UnitRace.NightElf or UnitRace.Gnome
            => PlayerFaction.Alliance,
        UnitRace.Orc or UnitRace.Undead or UnitRace.Tauren or UnitRace.Troll
            => PlayerFaction.Horde,
        UnitRace.None => throw new InvalidOperationException(
            "Forever player identity is unavailable or stale."),
        _ => throw new NotSupportedException(
            $"Forever faction is not known for race {race}.")
    };

    // 47 empty

    public Stance Stance { get; }
    public Form Form => Stance.Get(Class, bits.Stealthed(), Version);

    public int MinRange() => reader.GetInt(49) % 1000;
    public int MaxRange() => reader.GetInt(49) / 1000 % 1000;
    public bool MinRangeZero() => MinRange() == 0;

    public bool IsInMeleeRange() => MinRange() == 0 && MaxRange() != 0 && MaxRange() <= 5;
    public bool InCloseMeleeRange() => MinRange() == 0 && MaxRange() <= 2;

    public bool IsInDeadZone() => MinRange() >= 5 && SpellInRange.Target_Trade; // between 5-8 yard - hunter and warrior

    public RecordInt PlayerXp { get; } = new(50);

    public int PlayerMaxXp => reader.GetInt(51);
    public int PlayerXpPercent => (1 + PlayerXp.Value) * 100 / (1 + PlayerMaxXp);

    public int _PlayerXpPercent() => PlayerXpPercent;

    public RecordInt UIErrorTime { get; } = new(47);

    private UI_ERROR UIError => (UI_ERROR)reader.GetInt(52);
    public UI_ERROR LastUIError { get; set; }

    public int SpellBeingCast => reader.GetInt(53);
    public bool IsCasting() => SpellBeingCast != 0;

    // avgEquipDurability * 100 + target combo points
    public int ComboPoints() => reader.GetInt(54) % 100;
    public int AvgEquipDurability() => reader.GetInt(54) / 100; // 0-99

    public const int WeaponDurabilityCell = 116;

    // mainHandDurability * 10000 + offHandDurability * 100 + rangedDurability
    // Each is 0-99, where 99 also covers an empty slot or an equipped item that
    // has no durability - a thrown weapon, an off hand tome, a relic.
    public int MainHandDurability() => reader.GetInt(WeaponDurabilityCell) / 10000;
    public int OffHandDurability() => reader.GetInt(WeaponDurabilityCell) / 100 % 100;
    public int RangedDurability() => reader.GetInt(WeaponDurabilityCell) % 100;

    // The weapon closest to breaking. 99 = nothing equipped that can break.
    public int MinWeaponDurability() =>
        Math.Min(MainHandDurability(), Math.Min(OffHandDurability(), RangedDurability()));

    public AuraCount AuraCount => IsForever
        ? throw new NotSupportedException(
            "Forever aura counts are unavailable in combat.")
        : new(reader, 55);

    public int TargetId => reader.GetInt(56);
    public int TargetGuid => reader.GetInt(57);

    public int SpellBeingCastByTarget => reader.GetInt(58);
    public bool IsTargetCasting() => SpellBeingCastByTarget != 0;

    // 10 * MouseOverTarget + TargetTarget
    public UnitsTarget MouseOverTarget => (UnitsTarget)(reader.GetInt(59) / 10 % 10);
    public UnitsTarget TargetTarget => (UnitsTarget)(reader.GetInt(59) % 10);
    public bool TargetsMe() => TargetTarget == UnitsTarget.Me;
    public bool TargetsPet() => TargetTarget == UnitsTarget.Pet;
    public bool TargetsNone() => TargetTarget == UnitsTarget.None;

    public RecordInt AutoShot { get; } = new(60);
    public RecordInt MainHandSwing { get; } = new(61);
    public RecordInt CastEvent { get; } = new(62);
    public UI_ERROR CastState => (UI_ERROR)CastEvent.Value;
    public RecordInt CastSpellId { get; } = new(63);

    public const int PetTargetGuidCell = 69;

    public int PetGuid => reader.GetInt(68);
    public int PetTargetGuid => reader.GetInt(PetTargetGuidCell);
    public bool PetTarget() => PetTargetGuid != 0;

    public int CastCount => reader.GetInt(70);

    public BitVector32 CustomTrigger1;

    // 10000 * off * 100 + main * 100
    public int MainHandSpeedMs() => reader.GetInt(75) % 10000 * 10;
    public int OffHandSpeed => reader.GetInt(75) / 10000 * 10;

    public int RemainCastMs => reader.GetInt(76);


    // MouseOverLevel * 100 + MouseOverClassification
    public int MouseOverLevel => reader.GetInt(85) / 100;
    public UnitClassification MouseOverClassification => (UnitClassification)(reader.GetInt(85) % 100);
    public int MouseOverId => reader.GetInt(86);
    public int MouseOverGuid => reader.GetInt(87);

    public int FocusHealthMax() => ClassicAbsoluteVital(89);
    public int FocusHealthCurrent() => ClassicAbsoluteVital(90);
    public int FocusHealthPercent() => (1 + FocusHealthCurrent()) * 100 / (1 + FocusHealthMax());

    public int LastCastGCD { get; private set; }
    public void ResetLastCastGCD()
    {
        LastCastGCD = 0;
    }
    public void ReadLastCastGCD()
    {
        LastCastGCD = reader.GetInt(94);
    }

    public RecordInt GCD { get; } = new(95);

    public int NetworkLatency => reader.GetInt(96) % 10000;

    public int DoubleNetworkLatency => 2 * NetworkLatency;

    public int HalfNetworkLatency => NetworkLatency / 2;

    public int SpellQueueTimeMs => reader.GetInt(96) / 10000 % 10000;

    public int _SpellQueueTimeMs() => SpellQueueTimeMs;

    public int _SpellQueueTimeMsNegative() => -SpellQueueTimeMs;

    public int HalfSpellQueueTimeMs => SpellQueueTimeMs / 2;

    // Formula (10 * LootWindowCount) + LootEvent(0-9)
    public RecordInt LootEvent { get; } = new(97);
    public RecordInt LootWindowCount { get; } = new(97);

    public int FocusGuid => reader.GetInt(77);
    public int FocusTargetGuid => reader.GetInt(78);

    public int RangedSpeedMs() => reader.GetInt(88) * 10;

    public int SoftInteract_Guid => reader.GetInt(101);

    public float RunSpeed => reader.GetFixed20(111);

    private int previousHealthPercent;
    private long lastDamageTakenTimestamp;

    public int SinceDamageTakenMs() =>
        lastDamageTakenTimestamp == 0
            ? int.MaxValue
            : (int)Stopwatch.GetElapsedTime(lastDamageTakenTimestamp).TotalMilliseconds;

    public int SoftInteract_Id => reader.GetInt(102);

    public GuidType SoftInteract_Type => (GuidType)reader.GetInt(103);

    public void Update(IAddonDataProvider reader)
    {
        if (IsForever)
        {
            UpdateForeverVisual();
            return;
        }

        if (UIMapId.Updated(reader) && UIMapId.Value != 0 &&
            worldMapAreaDB.TryGet(UIMapId.Value, out WorldMapArea wma))
        {
            WorldMapArea = wma;
            MapId = wma.MapID;

            areaDb.Update(wma.AreaID);
        }

        CustomTrigger1 = new(reader.GetInt(74));

        PlayerXp.Update(reader);
        Level.Update(reader);

        AutoShot.Update(reader);
        MainHandSwing.Update(reader);
        CastEvent.Update(reader);
        CastSpellId.Update(reader);

        LootEvent.UpdateIncludeLeastSignificantDigit(reader, 10);
        LootWindowCount.UpdateExcludingLeastSignificantDigits(reader, 10);

        Money.Update(reader);

        GCD.Update(reader);

        UIErrorTime.Update(reader);

        if (UIError != UI_ERROR.NONE)
            LastUIError = UIError;

        int currentHealth = HealthPercent();
        if (currentHealth < previousHealthPercent && previousHealthPercent > 0)
        {
            lastDamageTakenTimestamp = Stopwatch.GetTimestamp();
        }
        previousHealthPercent = currentHealth;
    }

    private void UpdateForeverVisual()
    {
        if (TryGetForeverVisualVitals(out VisualBarSample bars))
        {
            int currentHealth = VisualPercent(bars.PlayerHealth);
            if (currentHealth < previousHealthPercent && previousHealthPercent > 0)
                lastDamageTakenTimestamp = Stopwatch.GetTimestamp();
            previousHealthPercent = currentHealth;
            Level.ForceUpdate(bars.PlayerIdentity?.Level ?? 0);
        }
        else
        {
            previousHealthPercent = 0;
            Level.ForceUpdate(0);
        }

        if (!TryGetForeverPosition(out VisualPositionSample position) ||
            !worldMapAreaDB.TryGet(position.UIMapId, out WorldMapArea wma))
        {
            UIMapId.ForceUpdate(0);
            WorldMapArea = default;
            MapId = 0;
            return;
        }

        if (UIMapId.Value == position.UIMapId)
            return;

        UIMapId.ForceUpdate(position.UIMapId);
        WorldMapArea = wma;
        MapId = wma.MapID;
        areaDb?.Update(wma.AreaID);
    }

    public void Reset()
    {
        UIMapId.Reset();

        // Reset all RecordInt
        AutoShot.Reset();
        MainHandSwing.Reset();
        CastEvent.Reset();
        CastSpellId.Reset();

        PlayerXp.Reset();
        Level.Reset();

        LootEvent.Reset();
        LootWindowCount.Reset();
        UIErrorTime.Reset();

        GCD.Reset();
    }

    public bool IsMeleeSwingingDefault() => IsMeleeSwinging(500);

    public bool IsMeleeSwinging(int extraMarginMs)
    {
        // Only relevant when in melee range
        if (!IsInMeleeRange())
            return false;

        // Check if swing timer shows recent activity
        int swingSpeed = MainHandSpeedMs();
        int elapsed = MainHandSwing.ElapsedMs();

        // Buffer: swing speed + network latency + 250ms margin
        int maxExpectedElapsed = swingSpeed + NetworkLatency + extraMarginMs;

        return elapsed < maxExpectedElapsed;
    }
}
