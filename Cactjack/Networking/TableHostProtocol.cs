using System.Collections.Generic;
using Cactjack.Game;

namespace Cactjack.Networking;

public sealed class TableHostProtocol
{
    public const int ProtocolVersion = 1;
    private readonly TableSession session;
    private readonly HashSet<string> processedRequests = new();

    public long Revision { get; private set; }

    public TableHostProtocol(TableSession session) => this.session = session;

    public bool Process(TableCommand command)
    {
        if (command.ExpectedRevision >= 0 && command.ExpectedRevision != Revision) return false;
        if (!string.IsNullOrEmpty(command.RequestId) && !processedRequests.Add(command.RequestId)) return false;

        if (command.Type == TableCommandType.JoinSeat && command.Value == 3)
        {
            if (session.Seats.Exists(seat => seat.IsOccupied && seat.ClientId == command.SenderId)) return false;
            var openIndex = session.Seats.FindIndex(seat => seat.IsOpen && !seat.IsOccupied);
            if (openIndex < 0) return false;
            command = command with { SeatIndex = openIndex };
        }

        var hostOnly = (command.Type == TableCommandType.JoinSeat && command.Value != 3) || command.Type is TableCommandType.KickSeat
            or TableCommandType.ToggleSeatOpen or TableCommandType.RenameSeat
            or TableCommandType.DisconnectSeat or TableCommandType.ReconnectSeat
            or TableCommandType.StartRound or TableCommandType.NextRound;
        if (hostOnly && command.SenderId != "host") return false;

        if (command.Type is TableCommandType.SetReady or TableCommandType.SetWager)
        {
            if (command.SeatIndex < 0 || command.SeatIndex >= session.Seats.Count) return false;
            if (command.SenderId != "host" && session.Seats[command.SeatIndex].ClientId != command.SenderId) return false;
        }

        if (command.Type == TableCommandType.LeaveSeat)
        {
            if (command.SeatIndex < 0 || command.SeatIndex >= session.Seats.Count) return false;
            if (session.Seats[command.SeatIndex].ClientId != command.SenderId) return false;
        }

        if (command.Type is TableCommandType.Hit or TableCommandType.Stand)
        {
            if (session.ActiveSeat is null) return false;
            var proxyingGuest = command.SenderId == "host" && session.ActiveSeat.IsGuest;
            if (session.ActiveSeat.ClientId != command.SenderId && !proxyingGuest) return false;
        }

        session.HandleCommand(command);
        Revision++;
        return true;
    }

    public TableSnapshot CreateSnapshot(string recipientId, bool hostView) =>
        session.CreateSnapshot(Revision, recipientId, hostView);
}
