# Cactjack

Cactjack is an experimental multiplayer blackjack table for FFXIV, built as a Dalamud plugin by CometTheFrog.

The current online alpha includes:

- Solo blackjack practice
- A local host/table simulator with dummy and chat-assisted guest seats
- A network lobby for true remote plugin-to-plugin play
- Host/dealer-only mode, private bankrolls, wagers, ready checks, and remote Hit/Stand actions
- Six-deck shoe gameplay with splits, doubles, dealer soft-17 behavior, and settlement

> **Online alpha:** This is an early testing build. Use play chips only. Expect interface and protocol changes, and do not treat balances as currency or permanent records.

## Requirements

- FINAL FANTASY XIV launched through XIVLauncher with Dalamud enabled
- Visual Studio 2022 with the .NET desktop workload, or the .NET SDK required by the project
- Dalamud must have been launched at least once so its development files are available

## Build

1. Clone this repository.
2. Open `Cactjack.slnx` in Visual Studio.
3. Select `Debug` and `x64`, then build the solution.
4. The development plugin is produced at `Cactjack/bin/x64/Debug/Cactjack.dll`.

For a distributable build, select `Release`; its files are produced under `Cactjack/bin/x64/Release/Cactjack/`.

## Install as a development plugin

1. In FFXIV, enter `/xlsettings`.
2. Open **Experimental** and add the full path to the built `Cactjack.dll` under **Dev Plugin Locations**.
3. Enter `/xlplugins`, open **Dev Tools → Installed Dev Plugins**, and enable **Cactjack**.
4. Enter `/cactjack` to open or close the table window.

## Easy tester installation

Testers who do not build plugins can install Cactjack through its custom testing repository:

1. In FFXIV, enter `/xlsettings`.
2. Open **Experimental** and scroll to **Custom Plugin Repositories**.
3. Add this URL and save:

   `https://raw.githubusercontent.com/CometTheFrog/Cactjack/master/pluginmaster.json`

4. Enter `/xlplugins`, search for **Cactjack**, and select **Install**.
5. Enter `/cactjack` to open the table.

This is an unofficial experimental repository. Remove the repository URL after testing if you do not want future alpha updates.

## Test a remote table

Both players need the same Cactjack build and an internet connection.

1. The dealer opens **Network Lobby**, enters a character name, and creates a table.
2. The dealer sends the displayed six-character room code to the other tester.
3. The player opens **Network Lobby**, enters their character name and the room code, then joins.
4. The player chooses a wager and marks Ready.
5. The dealer starts the round. The player uses their own Hit and Stand controls.

The hosted relay carries table messages between plugin instances. The dealer remains authoritative for cards and settlement; the relay does not store a permanent bankroll ledger.

## Chat-assisted guests

The local table simulator can represent a player who does not run Dalamud. Bind the guest seat to their party name and announce the hand in party chat. During their turn they can answer with:

- `cj hit`
- `cj stand`

The host must have the **Host** view selected for chat-assisted commands to be processed. Bankroll information remains private to the host unless the host chooses to share it.

## Relay development

The Cloudflare Worker relay source is in `relay/`. To validate it locally:

```text
cd relay
npm install
npm run check
```

Deployment requires a Cloudflare account and Wrangler authentication. No Cloudflare credentials are stored in this repository.

## Current alpha limitations

- Online play is intentionally small-table testing, not a public matchmaking service.
- Reconnect and failure handling are still being hardened.
- The user interface is functional and responsive work is ongoing.
- Splits and doubles should receive additional two-client regression testing before a wider release.

Please report the room role, action taken, and what each player saw when filing a test issue. Screenshots are especially useful.

## License

Cactjack is licensed under AGPL-3.0-or-later. See `LICENSE.md`.
