# Troubleshooting / FAQ

## Most Common Issue: Game Window Must Be Visible

**The bot reads pixel colors from the top-left corner of the WoW game window.** If anything covers this area, the bot cannot function properly.

**Symptoms:**
- Bot not responding to game state
- "Waiting for client" or similar messages
- Bot actions don't match what's happening in-game
- Screenshot component shows wrong/static image

**Solution:**
- **Keep the WoW window uncovered** - don't let browsers, Discord, or other windows overlap it
- Position the bot's web UI (browser) on a second monitor, or beside the game window
- If using one monitor, make the WoW window smaller and position it so the top-left corner is always visible
- Consider using **Windowed** mode instead of Fullscreen for easier window management
- **Use a fixed stable framerate** - set the game to a consistent FPS cap such as 15, 30, 60, 90, or 120. These values are tested and proven to provide a stable experience

**Technical explanation:** The addon encodes game state (health, mana, target info, etc.) as colored pixels in frames positioned at the top-left of the screen. The application captures these pixels via screenshot and decodes them. Any overlay or window covering these pixels will cause incorrect readings.

---

## Addon Issues

**Q: Addon not showing in WoW addon list**
- Ensure the addon folder is named correctly (should match the Title you configured)
- Check that the addon is in the correct `Interface\AddOns\` folder for your WoW version
- Make sure addon files are not inside an extra subfolder

**Q: "Addon blocked" or keybindings not working**
- You cannot set keybindings while in combat - log out and back in
- Run `/<prefix>actions` to manually create secure buttons (e.g., `/dcactions`)
- Check that you're not running conflicting addons

**Q: Pixel reading errors / wrong colors detected**
- **First check**: Is the WoW window top-left corner visible? See [Most Common Issue](#most-common-issue-game-window-must-be-visible) above
- The addon auto-configures graphics settings, but verify: Anti-Aliasing = None, Render Scale = 100%
- Make sure no screen overlays (Discord, GeForce Experience, Steam overlay) are affecting the game window
- Try running WoW in Windowed mode instead of Fullscreen
- Disable Windows HDR if enabled

## Application Issues

**Q: Application cannot find WoW process**
- Make sure WoW is running and you are logged into a character
- If running multiple WoW clients, use the `-p` parameter to specify the process ID

**Q: Path not found / navigation errors**
- Download the navmesh tiles for your era: `.\scripts\download-navmesh.ps1 -Era precata` (the scripts are scoped to Classic and Forever; see [navigation](navigation.md)). This is all that is needed — no game files, no external service.
- Check the startup log for `disk-only, no game files` per continent. If a continent is missing entirely, its tiles were not downloaded.
- If you baked your own tiles, the bot must run with the **same** bake settings that produced them. Bake settings name the cache directory, so a mismatch makes every baked tile invisible rather than wrong. Outland is the usual case: it needs `Navmesh:Bake:MinWorldZ: { "Expansion01": -700 }`, which is the shipped default.
- `Json\MPQ` is only needed to *bake* tiles yourself or to run the legacy `Pathing:Engine=SpotAStar`.

**Q: Class profile won't load**
- Check JSON syntax - use a JSON validator
- Ensure all required fields are present (see [Class Configuration](class-profiles.md#class-configuration))
- Check the log output for specific error messages

## Keybinding Issues

**Q: Keys not being pressed / wrong keys pressed**
- Check the Frontend "Key Bindings" page to verify detected bindings
- Use the key tester on the Key Bindings page to verify input is working
- For non-US keyboard layouts, avoid Main Action Bar slots 11-12 (see [Known Limitation](configuration.md#known-limitation-main-action-bar-slots-11-and-12))

**Q: Modifier keys (Shift/Ctrl/Alt) not working**
- Only single modifiers are supported, not combinations like Shift-Alt
- Verify the binding in WoW uses the same modifier
