# STS2 Twitch Overlay Mod

Broadcasts live Slay the Spire 2 game state to a Twitch extension so viewers can see your deck, relics, enemies, and more in real time.

---

## For players

### Prerequisites
- Slay the Spire 2 (Steam)
- A Twitch account and channel

### 1. Install the mod

Copy both files into your STS2 mods folder:

```
C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\mods\
```

- `TwitchOverlayMod.dll`
- `TwitchOverlayMod.json`

### 2. Authenticate

Launch STS2. A default config file is created automatically at `%APPDATA%\SlayTheSpire2\TwitchOverlayMod.config.json` if one does not exist. A Twitch icon button appears on the main menu below the profile selector.

| Color | Meaning |
|-------|---------|
| Purple | Not connected |
| Yellow | Connecting… |
| Green | Authenticated |
| Red | Error |

Click the button — your browser will open for Twitch OAuth. After authorizing, the button turns green and the mod begins broadcasting.

### 4. Install the Twitch extension

Install the extension from the [Twitch Extension Dashboard](https://dashboard.twitch.tv/extensions/zenmih23y97hx55rke73ecwyepd5vl-0.0.2) and activate it as a video overlay on your channel. Viewers will see your game state in real time.

---

## For developers — self-hosting

Use this section if you want to run your own EBS and register your own Twitch extension (e.g. to fork the overlay frontend or change the broadcast format).

### Prerequisites
- A Netlify account
- A Twitch developer account at [dev.twitch.tv](https://dev.twitch.tv)
- Node.js
- .NET 9 SDK

### 1. Register a Twitch Extension

1. Go to [dev.twitch.tv](https://dev.twitch.tv) → **Extensions** → **Create Extension**
2. Set type: **Video Overlay**
3. Under **Capabilities** → PubSub, enable **broadcast**
4. Note your **Client ID** and **Client Secret**
5. Add an OAuth redirect URI:
   ```
   https://<your-netlify-site>.netlify.app/.netlify/functions/callback
   ```

### 2. Deploy the EBS to Netlify

```bash
cd ebs
npm install
npx netlify deploy --prod
```

Alternatively, connect the repo in the Netlify dashboard and it will deploy automatically.

Then in **Netlify dashboard → Site configuration → Environment variables**, add:

| Variable | Value |
|----------|-------|
| `CLIENT_ID` | Twitch extension client ID |
| `CLIENT_SECRET` | Twitch extension client secret |
| `TWITCH_EXTENSION_SECRET` | Base64-encoded extension secret (from the Twitch dev console, **not** the client secret) |

The three serverless functions are:

| Endpoint | Role |
|----------|------|
| `/.netlify/functions/auth` | Starts the OAuth flow |
| `/.netlify/functions/callback` | Exchanges code, mints JWT, redirects to local callback |
| `/.netlify/functions/refresh` | Refreshes the broadcast JWT (30-day refresh tokens) |

### 3. Build the mod

The project references STS2 game DLLs from the default Steam install path.

```bash
dotnet build
```

The output DLL is copied automatically to the STS2 mods folder on build.

### 4. Configure the mod to point to your EBS

In `%APPDATA%\SlayTheSpire2\TwitchOverlayMod.config.json`:

```json
{
  "ebsUrl": "https://<your-netlify-site>.netlify.app",
  "extensionClientId": "<your Twitch extension client ID>",
  "broadcastIntervalSeconds": 2.0
}
```

---

## Broadcast payload

The mod sends a JSON payload every `broadcastIntervalSeconds` containing:

- **Player** — health, deck, relics, potions, gold
- **Combat** — enemies, intents, turn count
- **Map** — current location and map state
- **UI** — current screen

Cards, relics, enemies, and powers are referenced by sequential integer IDs defined in `data/*.json`. These IDs are assigned by the [STS2 Content Exporter mod](https://github.com/boardengineer/STS2ContentExporter) and must remain stable across exports.

---

## License

MIT
