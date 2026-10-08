using Core;

using HeadlessServer;
using HeadlessServer.Runtimes;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using SixLabors.ImageSharp;

using System;

using Xunit;

namespace AgentTests.Agent;

/// <summary>
/// What keeps the addon's frames from being read, in words a person can act on (known issue 66). The
/// probe deleted <c>frame_config.json</c> on any mismatch, a minimized window included, and refused
/// every later Classic run on that PC; HeadlessServer now refuses with the reason and keeps the file.
/// </summary>
public sealed class FrameMismatchTests
{
    private static readonly Version Addon = new(1, 7, 74);
    private static readonly Rectangle Window = new(0, 0, 1920, 1080);
    private static readonly DataFrameConfig Saved = new(FrameConfigMeta.Version, Addon, Window, DataFrameMeta.Empty,
        [new DataFrame(0, 0, 0), new DataFrame(1, 1, 0)]);

    [Fact]
    public void Frames_made_for_this_window_and_addon_match()
    {
        Assert.Null(FrameConfig.Mismatch(Saved, Window, Addon));
    }

    [Fact]
    public void A_minimized_window_is_named_as_such()
    {
        Assert.Equal("the WoW window is minimized: restore it", FrameConfig.Mismatch(Saved, new Rectangle(0, 0, 0, 0), Addon));
    }

    [Fact]
    public void A_resized_window_names_both_sizes()
    {
        string? mismatch = FrameConfig.Mismatch(Saved, new Rectangle(0, 0, 1280, 720), Addon);

        Assert.NotNull(mismatch);
        Assert.Contains("1280x720", mismatch);
        Assert.Contains("1920x1080", mismatch);
    }

    [Fact]
    public void Another_addon_another_format_and_no_frames_each_say_so()
    {
        DataFrameConfig older = new(FrameConfigMeta.Version - 1, Addon, Window, DataFrameMeta.Empty, Saved.Frames);
        DataFrameConfig empty = new(FrameConfigMeta.Version, Addon, Window, DataFrameMeta.Empty, []);

        Assert.Contains("1.7.75", FrameConfig.Mismatch(Saved, Window, new Version(1, 7, 75)));
        Assert.Contains("another format", FrameConfig.Mismatch(older, Window, Addon));
        Assert.Contains("no frames", FrameConfig.Mismatch(empty, Window, Addon));
    }

    [Fact]
    public void Load_only_refuses_without_the_configuration_and_never_looks_for_the_client()
    {
        if (ClassicRuntime.MissingConfiguration() is null)
        {
            Assert.Skip("this machine has the addon's configuration beside the tests: --loadonly would attach to WoW");
        }

        RunOptions options = new() { ClassConfig = "Warrior_1.json", LoadOnly = true };

        Assert.False(ClassicRuntime.LoadOnly("Warrior_1.json", options, new ConfigurationBuilder().Build(), NullLoggerFactory.Instance));
    }
}
