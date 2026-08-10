# Cactjack Online Alpha Test

Thank you for testing Cactjack. Both people should build or install the same version before starting.

## Quick test

1. Confirm `/cactjack` opens the window on both game clients.
2. Dealer: open **Network Lobby**, enter a name, and create a room.
3. Player: enter a name and the dealer's six-character room code, then join.
4. Confirm both clients show the same player and room.
5. Player: enter a wager and select Ready.
6. Dealer: deal the round.
7. Player: test Hit and Stand from the player's game client.
8. Confirm both clients show the same cards, result, wager, and next-round state.

## Useful failure tests

- Disconnect and reconnect from the lobby between rounds.
- Disconnect during a turn and confirm the dealer is not permanently stuck.
- Try a second round without recreating the room.
- Resize the window at betting and settlement.
- If offered, test Double and Split and compare both screens after every action.

## What to report

Include:

- Which person was dealer and which was player
- Whether the failure happened before, during, or after a hand
- The action immediately before the problem
- What each client displayed
- Screenshots from both clients when possible

Do not post Cloudflare credentials, private tokens, or real-money information in an issue.
