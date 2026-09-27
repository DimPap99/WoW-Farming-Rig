# Macros

**Why Macros?** WoW macros combine multiple actions into a single button press. The bot can use macros just like regular abilities - they're essential for complex actions like targeting NPCs for vendoring, combining pet commands, or using multiple ranks of the same item.

**Naming Convention**: When using macros in your class profile, use **lowercase** names (e.g., `"heal"`, `"feedpet"`) to distinguish them from actual spells. This helps the `ActionBarPopulator` correctly identify what to place on the action bar.

Warlock `heal` macro used in warlock profiles.
```css
#showtooltip Create Healthstone
/use Minor Healthstone
/use Lesser Healthstone
/use Healthstone
/use Greater Healthstone
/use Major Healthstone
/use Master Healthstone
/stopmacro [combat]
/cast [nocombat] Create Healthstone
```

Hunter `feedpet` macro replace `Roasted Quail` with the proper diet
```css
#showtooltip
/cast Feed Pet
/use Roasted Quail
```

Hunter `sumpet` macro
```css
#showtooltip
/cast [target=pet,dead] Revive Pet
/cast [target=pet,noexists] Call Pet
```

Rogue weapon enchant (use 17 for second weapon):
```css
#showtooltip
/use Instant Poison V
/use 16
/click StaticPopup1Button1
```

Melee weapon enchant:
```css
#showtooltip
/use Dense Sharpening Stone
/use 16
/click StaticPopup1Button1
```
