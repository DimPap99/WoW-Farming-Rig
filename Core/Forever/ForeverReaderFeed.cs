using System.Threading;

namespace Core.Forever;

/// <summary>
/// Forever has no Classic pixel strip. On that client AddonReader hands each
/// capture tick to this class, which refreshes the visual tracker and the few
/// shared readers the guarded runtime uses: player identity and map, the
/// spellbook and the key bindings.
/// </summary>
public sealed class ForeverReaderFeed
{
    private readonly ForeverVisualBarTracker tracker;
    private readonly IAddonDataProvider reader;
    private readonly PlayerReader playerReader;
    private readonly SpellBookReader spellBookReader;
    private readonly KeyBindingsReader keyBindingsReader;
    private readonly ManualResetEventSlim dataReady;
    private long lastSignalledFrame;

    public bool IsEnabled => tracker.IsEnabled;

    public ForeverReaderFeed(ForeverVisualBarTracker tracker,
        IAddonDataProvider reader, PlayerReader playerReader,
        SpellBookReader spellBookReader, KeyBindingsReader keyBindingsReader,
        ManualResetEventSlim dataReady)
    {
        this.tracker = tracker;
        this.reader = reader;
        this.playerReader = playerReader;
        this.spellBookReader = spellBookReader;
        this.keyBindingsReader = keyBindingsReader;
        this.dataReady = dataReady;

        // Resolve spell slots from checked live action IDs, and never infer a
        // key from Classic defaults when the live bindings say it is unbound.
        // The provider checks capture age on every call.
        KeyReader.ForeverActionBarProvider = IsEnabled
            ? () => tracker.TryGetLatest(out VisualBarSample bars) ? bars.ActionBar : null
            : null;
        KeyReader.RequireGameBindings = IsEnabled;
    }

    public void Update()
    {
        tracker.Update();
        playerReader.Update(reader);

        bool available = tracker.TryGetLatest(out VisualBarSample bars);
        spellBookReader.UpdateForever(available ? bars.SpellBook : null);
        keyBindingsReader.UpdateForever(available ? bars.Bindings : null);

        // The goal loop waits on DataReady. Signal it once per new frame that
        // carries every trial input; there is no GlobalTime cell to drive it.
        if (available && bars.FrameId != lastSignalledFrame &&
            ForeverFrame.HasTrialInputs(bars))
        {
            lastSignalledFrame = bars.FrameId;
            dataReady.Set();
        }
    }
}
