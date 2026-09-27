# Mail Goal

Automatically mail items and gold to another character when visiting a mailbox.

### Quick Setup

1. Enable mail in your class profile:
```json
{
  "Mail": true,
  "MailConfig": {
    "RecipientName": "YourAltCharacter"
  }
}
```

2. The bot will mail items after vendoring/repairing when:
   - Items meet the quality threshold (default: Green/Uncommon)
   - Player has excess gold above minimum threshold
   - Player is at a mailbox

### Configuration Options

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Mail` | bool | `false` | Enable/disable mail for this profile |
| `MailFilename` | string | `""` | External mail config file (relative to `Json/mail/`) |
| `MailConfig.RecipientName` | string | `""` | Character name to send mail to |
| `MailConfig.MinimumGoldToKeep` | int | `10000` | Minimum gold to keep (in copper, 10000 = 1g) |
| `MailConfig.MinimumItemQuality` | int | `2` | Minimum item quality (0=Grey, 1=White, 2=Green, 3=Blue, 4=Epic) |
| `MailConfig.SendGold` | bool | `true` | Whether to send excess gold |
| `MailConfig.SendItems` | bool | `true` | Whether to send items |
| `MailConfig.ExcludedItemIds` | int[] | `[]` | Item IDs to never mail |

### Recipient Priority

The recipient is determined in this order:
1. **UI Setting** (localStorage) - Set via Frontend Mail page
2. **Environment Variable** - `MAIL_RECIPIENT=CharacterName`
3. **JSON Config** - `MailConfig.RecipientName`

### Special Recipient Keywords

Instead of a character name, you can use special keywords:

| Keyword | Behavior |
|---------|----------|
| `UseRandomFriendList` | Randomly selects a character from your friend list (online or offline) |

**Example:**
```json
{
  "Mail": true,
  "MailConfig": {
    "RecipientName": "UseRandomFriendList",
    "MinimumItemQuality": 2
  }
}
```

**Note:** If using `UseRandomFriendList` and your friend list is empty, mail sending will be skipped.

### Example: Full Mail Configuration

```json
{
  "ClassName": "Warrior",
  "Mail": true,
  "MailConfig": {
    "RecipientName": "MyBankAlt",
    "MinimumGoldToKeep": 50000,
    "MinimumItemQuality": 2,
    "SendGold": true,
    "SendItems": true,
    "ExcludedItemIds": [6948, 5956, 4306]
  }
}
```

### Example: Using External Config File

```json
{
  "Mail": true,
  "MailFilename": "bank_alt_mail.json"
}
```

Create `Json/mail/bank_alt_mail.json`:
```json
{
  "RecipientName": "MyBankAlt",
  "MinimumGoldToKeep": 20000,
  "MinimumItemQuality": 1,
  "SendGold": true,
  "SendItems": true,
  "ExcludedItemIds": [6948]
}
```

### Item Quality Reference

| Value | Quality | Color |
|-------|---------|-------|
| 0 | Poor | Grey |
| 1 | Common | White |
| 2 | Uncommon | Green |
| 3 | Rare | Blue |
| 4 | Epic | Purple |
| 5 | Legendary | Orange |

### What Gets Mailed

**Mailed:**
- Items meeting quality threshold
- Not in excluded items list
- Tradable (not soulbound, not quest items)

**Never Mailed:**
- Soulbound items
- Quest items
- Bind-on-Pickup (BoP) items
- Items in exclusion list
- Hearthstone (recommended to exclude: 6948)

### Frontend Mail Page

The Mail page in the browser UI allows you to:
- Set recipient name (persists across sessions)
- Configure quality threshold and gold minimum
- Visually browse your inventory
- Click items to add/remove from exclusion list
- Save settings to profile or use runtime overrides
