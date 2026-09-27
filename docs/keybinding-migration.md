# Important: Migration Guide for Existing Users

If you are upgrading from a previous version, please read this section carefully.

## Breaking Changes

### Keybinding System Overhaul

The application now **automatically reads your in-game keybindings** instead of requiring manual configuration. This is a significant improvement but requires some migration steps.

**What Changed:**
1. The addon now sends your actual WoW keybindings to the application
2. Modifier keys (Shift, Ctrl, Alt) are now fully supported
3. BindPad addon is bundled and used internally for secure macro buttons
4. Custom actions (StopAttack, ClearTarget) now use Alt-modified keys by default

**Migration Steps:**

1. **Update the Addon**: Copy the new `DataToColor` addon to your WoW Addons folder, replacing the old version.

2. **Install BindPad**: Copy the `BindPad` addon from the `Addons/BindPad/` folder to your WoW Addons folder. This is required for TBC Classic 2.5.5+ compatibility.

3. **First Login**: On first login after the update, the addon will:
   - Automatically set up essential keybindings if they are missing
   - Create secure action buttons for StopAttack, ClearTarget, etc.
   - Read and send all your keybindings to the application

4. **Check Your Class Profile**: The default keys for some BaseActions have changed:
   | Action | Old Default | New Default |
   | --- | --- | --- |
   | Interact | `I` | `Alt-Home` |
   | InteractMouseOver | `J` | `Alt-End` |
   | ClearTarget | `Insert` | `Alt-Insert` |
   | StopAttack | `Delete` | `Alt-Delete` |
   | TargetFocus | `PageUp` | `Alt-PageUp` |
   | FollowTarget | `PageDown` | `Alt-PageDown` |

   If your class profile overrides these keys, you may need to update them.

5. **In-Game Verification**: After logging in, you can verify the bindings are working:
   - Press `Shift-PageUp` to toggle addon config mode (should see "Config mode" / "Normal mode" messages)
   - Press `Shift-PageDown` to flush addon state (should see "Flush State" message)

### Troubleshooting

If bindings are not working after migration:
1. Make sure you are not in combat when logging in (bindings cannot be set in combat)
2. Run `/<prefix>actions` to manually create and bind the custom actions (e.g., `/dcactions`)
3. Run `/<prefix>bindings` to set up default action bar bindings (e.g., `/dcbindings`)
4. Check the Frontend "Key Bindings" page to see what bindings the addon detected

**Note**: The command prefix (e.g., `dc`) is derived from your addon title. If you named your addon "daq" during setup, use `/daqactions`, `/daqbindings`, etc.
