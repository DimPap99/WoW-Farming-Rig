# Interrupt Requirement

**Why Interrupt Requirements?** Some abilities take time (channeling, long casts, movement abilities). Interrupt Requirements let you abort these early when conditions change - for example, stop casting a heal if the target dies, or stop kiting when the enemy starts casting.

Every [KeyAction](class-profiles.md#keyaction) has individual Interrupt(s) condition(s) which are [Requirement(s)](requirements.md#requirement) to stop execution before fully finishing it.

As of now every [Goal groups](goals.md#goal-groups) has a default Interrupt.
* [Flee Goal](goals.md#flee-goal) based [KeyAction(s)](class-profiles.md#keyaction) interrupted once the player left combat.
* [Combat Goal](goals.md#combat-goal) based [KeyAction(s)](class-profiles.md#keyaction) interrupted once the target dies and the player loses the target.
* [Parallel Goal](goals.md#parallel-goals) based [KeyAction(s)](class-profiles.md#keyaction) has **No** interrupt conditions.
* [Adhoc Goals](goals.md#adhoc-goals) based [KeyAction(s)](class-profiles.md#keyaction) depends on `KeyAction.InCombat` flag.
* [Assist Focus Goal](goals.md#assist-focus-goal) based [KeyAction(s)](class-profiles.md#keyaction) interrupted once the target dies and the player loses the target.

Here's an example for low level Hunters, while playing without a pet companion take advantage of interrupt.

It attempts to step back for a maximum **3000**ms duration however this can be interrupted either
* losing the current target
* the next Auto Shot coming in **500**ms

This **500**ms duration is the reload animation time, while the player has to stay still.

```json
"Combat": {
    "Sequence": [
        //...
        {
        "Name": "Stepback",
        "Key": "S",
        "PressDuration": 3000,
        "BaseAction": true,
        "Requirements": [
            "LastAutoShotMs < 400",
            "!InMeleeRange",
            "AutoShot"
        ],
        "Interrupt": "RangedSwing < -500 && TargetAlive"
        },
        //...
    ]
}
```
---

Execute low hp enemy instead of awaiting the currently casted spell.

The key takeaway here is that the `Earth Shock` spell has higher priority.

```json
"Combat": {
    "Sequence": [
        //...
        {
            "Name": "Earth Shock",
            "Key": "3",
            "Requirements": [
                "SpellInRange:1",
                "TargetAlive && TargetHealth% < 20"
            ]
        },
        {
            "Name": "Lightning Bolt",
            "Key": "1",
            "HasCastBar": true,
            "AfterCastWaitCastbar": true,
            "Requirement": "!Clearcasting",
            "Interrupt": "Clearcasting || CanRun:Earth Shock",
            "CancelOnInterrupt": true
        }
    ]
}
```
---
