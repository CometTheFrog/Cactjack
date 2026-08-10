using System.Collections.Generic;
using Cactjack.Game;

namespace Cactjack.Networking;

public sealed record SeatSnapshot(
    int Number,
    bool IsOpen,
    bool IsOccupied,
    bool IsLocal,
    string ClientId,
    bool IsGuest,
    bool IsConnected,
    string Name,
    int Chips,
    int Wager,
    bool IsReady,
    IReadOnlyList<Card> Cards,
    int Score,
    string Result);

public sealed record TableSnapshot(
    int ProtocolVersion,
    long Revision,
    string RecipientId,
    bool IsHostView,
    bool CanManageSeats,
    bool ViewerCanAct,
    string JoinCode,
    TablePhase Phase,
    int ActiveSeatIndex,
    int CardsRemaining,
    bool CanStart,
    IReadOnlyList<Card> VisibleDealerCards,
    bool DealerHoleCardHidden,
    int VisibleDealerScore,
    IReadOnlyList<SeatSnapshot> Seats,
    IReadOnlyList<string> Events);
