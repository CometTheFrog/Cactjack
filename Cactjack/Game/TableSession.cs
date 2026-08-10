using System;
using System.Collections.Generic;
using System.Linq;
using Cactjack.Networking;

namespace Cactjack.Game;

public enum TablePhase { Lobby, Betting, PlayerTurns, Settlement }

public sealed class TableSeat
{
    public int Number { get; init; }
    public bool IsOpen { get; set; } = true;
    public bool IsOccupied { get; set; }
    public bool IsLocal { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public bool IsGuest { get; set; }
    public bool IsAutomated { get; set; }
    public bool IsConnected { get; set; } = true;
    public string Name { get; set; } = string.Empty;
    public int Chips { get; set; } = 100_000_000;
    public int Wager { get; set; } = 100_000;
    public bool IsReady { get; set; }
    public bool IsFinished { get; set; }
    public bool IsInRound { get; set; }
    public List<Card> Cards { get; } = new();
    public string Result { get; set; } = string.Empty;
    public int Score => BlackjackGame.Score(Cards);
}

public sealed class TableSession
{
    private const int CutCardRemaining = 52;
    private readonly Random random = new();
    private readonly List<Card> shoe = new();

    public List<TableSeat> Seats { get; } = new();
    public List<Card> DealerHand { get; } = new();
    public List<string> Events { get; } = new();
    public TablePhase Phase { get; private set; } = TablePhase.Lobby;
    public int ActiveSeatIndex { get; private set; } = -1;
    public int CardsRemaining => shoe.Count;
    public int DealerScore => BlackjackGame.Score(DealerHand);
    public string JoinCode { get; } = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
    public TableSeat? ActiveSeat => ActiveSeatIndex >= 0 ? Seats[ActiveSeatIndex] : null;

    public TableSession()
    {
        for (var index = 0; index < 4; index++) Seats.Add(new TableSeat { Number = index + 1 });
        ShuffleShoe();
        Log("Host created a dealer-only table.");
    }

    public TableSnapshot CreateSnapshot(long revision = 0, string recipientId = "player-you", bool isHostView = true)
    {
        var revealDealer = Phase == TablePhase.Settlement;
        var dealerCards = revealDealer
            ? DealerHand.ToArray()
            : DealerHand.Count > 0 ? new[] { DealerHand[0] } : Array.Empty<Card>();
        var seats = new List<SeatSnapshot>(Seats.Count);
        foreach (var seat in Seats)
        {
            var visibleChips = isHostView || seat.ClientId == recipientId ? seat.Chips : -1;
            seats.Add(new SeatSnapshot(
                seat.Number, seat.IsOpen, seat.IsOccupied, seat.ClientId == recipientId, seat.ClientId, seat.IsGuest, seat.IsConnected, seat.Name,
                visibleChips, seat.Wager, seat.IsReady, seat.Cards.ToArray(), seat.Score, seat.Result));
        }
        return new TableSnapshot(
            TableHostProtocol.ProtocolVersion, revision, recipientId, isHostView,
            isHostView, ActiveSeatIndex >= 0 && Seats[ActiveSeatIndex].ClientId == recipientId, JoinCode,
            Phase, ActiveSeatIndex, CardsRemaining, CanStart, dealerCards,
            !revealDealer && DealerHand.Count > 1,
            BlackjackGame.Score(dealerCards), seats, Events.ToArray());
    }

    public void HandleCommand(TableCommand command)
    {
        switch (command.Type)
        {
            case TableCommandType.JoinSeat:
                if (command.Value == 2)
                    JoinSeat(command.SeatIndex, "You", true, "player-you");
                else if (command.Value == 3)
                    JoinSeat(command.SeatIndex, command.Text, false, command.SenderId);
                else
                    JoinSeat(command.SeatIndex, command.Text, false,
                        command.Value == 1 ? $"guest-{command.SeatIndex}" : $"dummy-{command.SeatIndex}",
                        command.Value == 1,
                        command.Value == 0);
                break;
            case TableCommandType.KickSeat:
                KickSeat(command.SeatIndex);
                break;
            case TableCommandType.ToggleSeatOpen:
                ToggleSeatOpen(command.SeatIndex);
                break;
            case TableCommandType.SetReady:
                SetReady(command.SeatIndex, command.Value != 0);
                break;
            case TableCommandType.SetWager:
                SetWager(command.SeatIndex, command.Value);
                break;
            case TableCommandType.RenameSeat:
                if (command.SeatIndex >= 0 && command.SeatIndex < Seats.Count && Seats[command.SeatIndex].IsGuest)
                    Seats[command.SeatIndex].Name = command.Text;
                break;
            case TableCommandType.DisconnectSeat:
                DisconnectSeat(command.SeatIndex);
                break;
            case TableCommandType.ReconnectSeat:
                ReconnectSeat(command.SeatIndex);
                break;
            case TableCommandType.LeaveSeat:
                LeaveSeat(command.SeatIndex);
                break;
            case TableCommandType.StartRound:
                StartRound();
                break;
            case TableCommandType.Hit:
                Hit();
                break;
            case TableCommandType.Stand:
                Stand();
                break;
            case TableCommandType.NextRound:
                NextRound();
                break;
        }
    }

    public void SetWager(int index, int wager)
    {
        var seat = Seats[index];
        if (!seat.IsOccupied || Phase == TablePhase.PlayerTurns) return;
        seat.Wager = Math.Clamp(wager, 1, Math.Max(1, seat.Chips));
        if (seat.IsReady) seat.IsReady = false;
    }

    public void JoinSeat(int index, string name, bool local = false, string clientId = "", bool guest = false, bool automated = false)
    {
        var seat = Seats[index];
        if (!seat.IsOpen || seat.IsOccupied || Phase is TablePhase.PlayerTurns) return;
        seat.IsOccupied = true;
        seat.IsLocal = local;
        seat.ClientId = string.IsNullOrEmpty(clientId) ? $"client-{index}" : clientId;
        seat.IsGuest = guest;
        seat.IsAutomated = automated;
        seat.IsConnected = true;
        seat.Name = name;
        seat.IsReady = !local;
        Log($"{name} joined seat {seat.Number}.");
        Phase = TablePhase.Betting;
    }

    public void KickSeat(int index)
    {
        var seat = Seats[index];
        if (!seat.IsOccupied || seat.IsLocal) return;
        Log($"Host removed {seat.Name} from seat {seat.Number}.");
        var wasActive = Phase == TablePhase.PlayerTurns && ActiveSeatIndex == index;
        ClearSeat(seat);
        if (wasActive) AdvanceTurn();
    }

    public void DisconnectSeat(int index)
    {
        var seat = Seats[index];
        if (!seat.IsOccupied || !seat.IsConnected) return;
        seat.IsConnected = false;
        seat.IsReady = false;
        Log($"{seat.Name} disconnected.");
        if (Phase == TablePhase.PlayerTurns)
        {
            seat.IsFinished = true;
            seat.Result = "Disconnected — hand forfeited.";
            if (ActiveSeatIndex == index) AdvanceTurn();
        }
    }

    public void ReconnectSeat(int index)
    {
        var seat = Seats[index];
        if (!seat.IsOccupied || seat.IsConnected) return;
        seat.IsConnected = true;
        Log($"{seat.Name} reconnected to seat {seat.Number}.");
    }

    public void LeaveSeat(int index)
    {
        var seat = Seats[index];
        if (!seat.IsOccupied || seat.IsLocal) return;
        Log($"{seat.Name} left the table.");
        var wasActive = Phase == TablePhase.PlayerTurns && ActiveSeatIndex == index;
        ClearSeat(seat);
        if (wasActive) AdvanceTurn();
    }

    public void ToggleSeatOpen(int index)
    {
        var seat = Seats[index];
        if (seat.IsOccupied || Phase == TablePhase.PlayerTurns) return;
        seat.IsOpen = !seat.IsOpen;
        Log($"Seat {seat.Number} {(seat.IsOpen ? "opened" : "closed")}.");
    }

    public void SetReady(int index, bool ready)
    {
        var seat = Seats[index];
        if (!seat.IsOccupied || Phase == TablePhase.PlayerTurns) return;
        seat.Wager = Math.Clamp(seat.Wager, 1, Math.Max(1, seat.Chips));
        seat.IsReady = ready;
        Log($"{seat.Name} is {(ready ? "ready" : "not ready")}.");
    }

    public bool CanStart => Phase != TablePhase.PlayerTurns && Seats.Exists(s => s.IsOccupied && s.IsConnected)
                            && Seats.FindAll(s => s.IsOccupied && s.IsConnected).TrueForAll(s => s.IsReady && s.Chips >= s.Wager);

    public void StartRound()
    {
        if (!CanStart) return;
        if (shoe.Count <= CutCardRemaining) ShuffleShoe();
        DealerHand.Clear();
        foreach (var seat in Seats)
        {
            seat.Cards.Clear(); seat.Result = string.Empty;
            seat.IsInRound = seat.IsOccupied && seat.IsConnected;
            seat.IsFinished = !seat.IsInRound;
            if (seat.IsInRound) { seat.Chips -= seat.Wager; seat.Cards.Add(Draw()); }
        }
        DealerHand.Add(Draw());
        foreach (var seat in Seats) if (seat.IsInRound) seat.Cards.Add(Draw());
        DealerHand.Add(Draw());
        Phase = TablePhase.PlayerTurns;
        Log("Host dealt a new round.");
        ActiveSeatIndex = -1;
        AdvanceTurn();
    }

    public void Hit()
    {
        var seat = ActiveSeat;
        if (seat is null) return;
        seat.Cards.Add(Draw()); Log($"{seat.Name} hits.");
        if (seat.Score >= 21) { seat.IsFinished = true; AdvanceTurn(); }
    }

    public void Stand()
    {
        var seat = ActiveSeat;
        if (seat is null) return;
        seat.IsFinished = true; Log($"{seat.Name} stands on {seat.Score}."); AdvanceTurn();
    }

    public void NextRound()
    {
        if (Phase != TablePhase.Settlement) return;
        foreach (var seat in Seats) if (seat.IsOccupied) seat.IsReady = seat.IsConnected && !seat.IsLocal;
        Phase = TablePhase.Betting;
        Log("Table returned to betting.");
    }

    private void AdvanceTurn()
    {
        for (var index = ActiveSeatIndex + 1; index < Seats.Count; index++)
        {
            var seat = Seats[index];
            if (!seat.IsInRound || seat.IsFinished) continue;
            ActiveSeatIndex = index;
            if (!seat.IsAutomated) { Log($"It is {seat.Name}'s turn."); return; }
            PlayDummy(seat);
            return;
        }
        PlayDealerAndSettle();
    }

    private void PlayDummy(TableSeat seat)
    {
        while (seat.Score < 17) seat.Cards.Add(Draw());
        seat.IsFinished = true;
        Log($"{seat.Name} finishes with {seat.Score}.");
        AdvanceTurn();
    }

    private void PlayDealerAndSettle()
    {
        while (DealerScore < 17) DealerHand.Add(Draw());
        foreach (var seat in Seats)
        {
            if (!seat.IsInRound) continue;
            if (!seat.IsConnected) seat.Result = "Disconnected — lost.";
            else if (seat.Score > 21) seat.Result = "Bust — lost.";
            else if (DealerScore > 21 || seat.Score > DealerScore) { seat.Result = "Won!"; seat.Chips += seat.Wager * 2; }
            else if (seat.Score == DealerScore) { seat.Result = "Push."; seat.Chips += seat.Wager; }
            else seat.Result = "Lost.";
            Log($"{seat.Name}: {seat.Result}");
        }
        ActiveSeatIndex = -1;
        Phase = TablePhase.Settlement;
        Log($"Dealer settles on {DealerScore}.");
    }

    private void ShuffleShoe()
    {
        shoe.Clear();
        for (var deck = 0; deck < 6; deck++)
        foreach (var suit in Enum.GetValues<Suit>())
        foreach (var rank in Enum.GetValues<Rank>()) shoe.Add(new Card(rank, suit));
        for (var i = shoe.Count - 1; i > 0; i--) { var j = random.Next(i + 1); (shoe[i], shoe[j]) = (shoe[j], shoe[i]); }
        Log("Six-deck shoe shuffled.");
    }

    private Card Draw() { var i = shoe.Count - 1; var card = shoe[i]; shoe.RemoveAt(i); return card; }
    private void Log(string text) { Events.Add(text); if (Events.Count > 40) Events.RemoveAt(0); }
    private static void ClearSeat(TableSeat seat)
    {
        seat.IsOccupied = false; seat.IsLocal = false; seat.ClientId = string.Empty; seat.IsGuest = false; seat.IsAutomated = false; seat.IsConnected = true; seat.Name = string.Empty; seat.IsReady = false;
        seat.Cards.Clear(); seat.Result = string.Empty; seat.IsInRound = false; seat.Chips = 100_000_000; seat.Wager = 100_000;
    }
}
