using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Cactjack.Game;
using Cactjack.Networking;
using Dalamud.Bindings.ImGui;

namespace Cactjack.Windows;

public sealed class NetworkLobbyView : IDisposable
{
    private static readonly Vector2 CardSize = new(52, 68);
    private readonly TablePresentationTimeline presentation = new();
    private WebSocketTableTransport? transport;
    private TableSession? hostSession;
    private TableHostProtocol? hostProtocol;
    private TableSnapshot? snapshot;
    private string roomCode = string.Empty;
    private string playerName = "Player";
    private string clientId = string.Empty;
    private string status = "Not connected";
    private bool busy;
    private bool isHost;

    public void Draw()
    {
        ImGui.Text("CACTJACK ONLINE TABLE");
        ImGui.Separator();
        if (transport is null) DrawConnectionForm();
        else DrawConnectedTable();
    }

    public void Dispose() => Disconnect();

    private void DrawConnectionForm()
    {
        ImGui.TextWrapped("Create a table, or enter a six-character code from another Cactjack host.");
        ImGui.Spacing();
        ImGui.SetNextItemWidth(220);
        ImGui.InputText("Character name", ref playerName, 64);
        if (!busy)
        {
            if (ImGui.Button("Create Table", new Vector2(145, 34))) _ = CreateTableAsync();
            ImGui.Spacing();
            ImGui.SetNextItemWidth(120);
            ImGui.InputText("Join code", ref roomCode, 6);
            ImGui.SameLine();
            if (ImGui.Button("Join Table", new Vector2(125, 34))) _ = JoinTableAsync();
        }
        DrawStatus();
        if (busy) ImGui.TextDisabled("Contacting Cactjack relay...");
    }

    private void DrawConnectedTable()
    {
        presentation.Update();
        ImGui.Text($"Room: {roomCode}  |  {(isHost ? "Host / Dealer" : playerName)}");
        ImGui.SameLine();
        if (ImGui.SmallButton("Copy Code")) ImGui.SetClipboardText(roomCode);
        ImGui.SameLine();
        if (ImGui.SmallButton("Disconnect")) Disconnect();
        ImGui.SameLine();
        DrawStatus();
        ImGui.SameLine();
        if (ImGui.SmallButton($"Animation: {presentation.MotionMode}"))
            presentation.MotionMode = presentation.MotionMode switch
            {
                PresentationMotionMode.Normal => PresentationMotionMode.Fast,
                PresentationMotionMode.Fast => PresentationMotionMode.Reduced,
                _ => PresentationMotionMode.Normal
            };

        if (snapshot is null)
        {
            ImGui.TextDisabled("Waiting for the host snapshot...");
            return;
        }

        ImGui.Separator();
        foreach (var seat in snapshot.Seats)
        {
            if (!seat.IsOccupied) continue;
            var bankroll = seat.Chips >= 0 ? seat.Chips.ToString("N0") : "Private";
            ImGui.Text($"Seat {seat.Number}: {seat.Name}  |  Wager {seat.Wager:N0}  |  Chips {bankroll}  |  {(seat.IsReady ? "Ready" : "Not ready")}");
        }

        if (snapshot.Phase is TablePhase.Lobby or TablePhase.Betting)
        {
            var ownSeat = snapshot.Seats.FirstOrDefault(seat => seat.IsLocal);
            if (ownSeat is not null)
            {
                var wager = ownSeat.Wager;
                ImGui.SetNextItemWidth(180);
                if (ImGui.InputInt("Your wager", ref wager, 10_000, 100_000))
                    SendPlayerCommand(TableCommandType.SetWager, ownSeat.Number - 1, wager);
                var ready = ownSeat.IsReady;
                if (ImGui.Checkbox("Ready", ref ready))
                    SendPlayerCommand(TableCommandType.SetReady, ownSeat.Number - 1, ready ? 1 : 0);
            }
            if (isHost)
            {
                if (snapshot.CanStart && ImGui.Button("Deal Round", new Vector2(135, 34))) SendHostCommand(TableCommandType.StartRound);
                else if (!snapshot.CanStart) ImGui.TextDisabled("Waiting for all connected players to ready.");
            }
        }
        else
        {
            DrawHands();
            if (snapshot.Phase == TablePhase.PlayerTurns && snapshot.ViewerCanAct && !presentation.IsAnimating)
            {
                if (ImGui.Button("Hit", new Vector2(90, 34))) SendPlayerCommand(TableCommandType.Hit);
                ImGui.SameLine();
                if (ImGui.Button("Stand", new Vector2(90, 34))) SendPlayerCommand(TableCommandType.Stand);
            }
            if (isHost && snapshot.Phase == TablePhase.Settlement && !presentation.IsAnimating
                && ImGui.Button("Return to Betting", new Vector2(160, 34)))
                SendHostCommand(TableCommandType.NextRound);
        }
    }

    private void DrawHands()
    {
        var current = snapshot!;
        ImGui.Spacing();
        ImGui.Text("DEALER");
        DrawCards(current.VisibleDealerCards, presentation.VisibleDealerCards, -1, presentation.ShowDealerHoleBack);
        var dealerTotal = presentation.IsAnimating && current.Phase == TablePhase.Settlement
            ? BlackjackGame.Score(current.VisibleDealerCards.Take(presentation.VisibleDealerCards))
            : current.VisibleDealerScore;
        ImGui.Text($"Total: {dealerTotal}");
        ImGui.Separator();
        foreach (var seat in current.Seats)
        {
            if (!seat.IsOccupied || seat.Cards.Count == 0) continue;
            var seatIndex = seat.Number - 1;
            var active = current.ActiveSeatIndex == seatIndex ? "> " : string.Empty;
            ImGui.Text($"{active}{seat.Name}  •  Wager {seat.Wager:N0}");
            var visibleCount = presentation.VisibleCardsForSeat(seatIndex);
            DrawCards(seat.Cards, visibleCount, seatIndex, false);
            var visibleScore = BlackjackGame.Score(seat.Cards.Take(visibleCount));
            var result = presentation.IsAnimating ? string.Empty : $"  {seat.Result}";
            ImGui.Text($"Total: {visibleScore}{result}");
            ImGui.Spacing();
        }
        if (presentation.IsAnimating)
            ImGui.TextColored(new Vector4(0.95f, 0.78f, 0.3f, 1f), "Dealing...");
    }

    private void DrawCards(System.Collections.Generic.IReadOnlyList<Card> cards, int visibleCount, int ownerIndex, bool addHoleBack)
    {
        var drewAny = false;
        for (var index = 0; index < Math.Min(visibleCount, cards.Count); index++)
        {
            if (drewAny) ImGui.SameLine();
            var isRevealing = presentation.RevealingSeatIndex == ownerIndex && presentation.RevealingCardIndex == index;
            DrawFaceCard(cards[index], $"card-{ownerIndex}-{index}", isRevealing ? presentation.StepProgress : 1f);
            drewAny = true;
        }
        if (presentation.PendingSeatIndex == ownerIndex && presentation.PendingCardIndex >= visibleCount)
        {
            if (drewAny) ImGui.SameLine();
            var slideProgress = EaseOutCubic(presentation.StepProgress);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (1f - slideProgress) * 42f);
            DrawBackCard($"pending-{ownerIndex}", 0.72f + slideProgress * 0.28f);
            drewAny = true;
        }
        if (addHoleBack)
        {
            if (drewAny) ImGui.SameLine();
            DrawBackCard("dealer-hole", 1f);
        }
    }

    private static void DrawFaceCard(Card card, string id, float flipProgress)
    {
        var red = card.Suit is Suit.Hearts or Suit.Diamonds;
        ImGui.PushStyleColor(ImGuiCol.Text, red ? new Vector4(0.85f, 0.08f, 0.08f, 1f) : new Vector4(0.08f, 0.08f, 0.1f, 1f));
        PushCardColors(new Vector4(0.94f, 0.91f, 0.82f, 1f));
        var width = CardSize.X * Math.Max(0.08f, EaseOutCubic(flipProgress));
        ImGui.Button($"{card}##{id}", new Vector2(width, CardSize.Y));
        ImGui.PopStyleColor(4);
    }

    private static void DrawBackCard(string id, float scale)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.95f, 0.78f, 0.3f, 1f));
        PushCardColors(new Vector4(0.32f, 0.07f, 0.08f, 1f));
        ImGui.Button($"♣##{id}", new Vector2(CardSize.X * scale, CardSize.Y * scale));
        ImGui.PopStyleColor(4);
    }

    private static float EaseOutCubic(float value)
    {
        var inverse = 1f - Math.Clamp(value, 0f, 1f);
        return 1f - inverse * inverse * inverse;
    }

    private static void PushCardColors(Vector4 color)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, color);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, color);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, color);
    }

    private async Task CreateTableAsync()
    {
        busy = true; status = "Creating room...";
        try
        {
            var room = await WebSocketTableTransport.CreateRoomAsync();
            roomCode = room.Code; isHost = true; clientId = "host";
            hostSession = new TableSession();
            hostProtocol = new TableHostProtocol(hostSession);
            ApplySnapshot(hostProtocol.CreateSnapshot("host", true));
            transport = CreateTransport();
            transport.CommandReceived += OnRemoteCommand;
            await transport.ConnectHostAsync(room, clientId);
        }
        catch (Exception exception) { Fail(exception); }
        finally { busy = false; }
    }

    private async Task JoinTableAsync()
    {
        if (roomCode.Trim().Length != 6) { status = "Error: enter a six-character room code."; return; }
        busy = true; status = "Joining room...";
        try
        {
            isHost = false; clientId = $"player-{Guid.NewGuid():N}";
            transport = CreateTransport();
            transport.SnapshotReceived += OnSnapshot;
            await transport.ConnectPlayerAsync(roomCode, clientId);
            transport.Send(new TableCommand(TableCommandType.JoinSeat, -1, 3, playerName, clientId, Guid.NewGuid().ToString("N"), -1));
        }
        catch (Exception exception) { Fail(exception); }
        finally { busy = false; }
    }

    private WebSocketTableTransport CreateTransport()
    {
        var next = new WebSocketTableTransport();
        next.StatusChanged += OnStatusChanged;
        return next;
    }

    private void OnRemoteCommand(TableCommand command)
    {
        if (hostProtocol?.Process(command) != true) return;
        PublishAllSnapshots();
    }

    private void PublishAllSnapshots()
    {
        if (hostProtocol is null || hostSession is null || transport is null) return;
        ApplySnapshot(hostProtocol.CreateSnapshot("host", true));
        foreach (var seat in hostSession.Seats.Where(seat => seat.IsOccupied && seat.ClientId.StartsWith("player-", StringComparison.Ordinal)))
            transport.Publish(hostProtocol.CreateSnapshot(seat.ClientId, false));
    }

    private void SendHostCommand(TableCommandType type)
    {
        if (hostProtocol is null) return;
        if (hostProtocol.Process(new TableCommand(type, SenderId: "host", RequestId: Guid.NewGuid().ToString("N"), ExpectedRevision: hostProtocol.Revision)))
            PublishAllSnapshots();
    }

    private void SendPlayerCommand(TableCommandType type, int seat = -1, int value = 0)
    {
        if (snapshot is null || transport is null) return;
        transport.Send(new TableCommand(type, seat, value, SenderId: clientId, RequestId: Guid.NewGuid().ToString("N"), ExpectedRevision: snapshot.Revision));
    }

    private void OnSnapshot(TableSnapshot next) => ApplySnapshot(next);
    private void ApplySnapshot(TableSnapshot next)
    {
        presentation.Apply(snapshot, next);
        snapshot = next;
    }
    private void OnStatusChanged(string next) => status = next;

    private void Disconnect()
    {
        if (transport is not null)
        {
            transport.StatusChanged -= OnStatusChanged;
            transport.CommandReceived -= OnRemoteCommand;
            transport.SnapshotReceived -= OnSnapshot;
        }
        transport?.Dispose(); transport = null; hostSession = null; hostProtocol = null; snapshot = null;
        status = "Not connected"; isHost = false;
    }

    private void Fail(Exception exception) { status = $"Error: {exception.Message}"; transport?.Dispose(); transport = null; }

    private void DrawStatus()
    {
        var color = status == "Connected" ? new Vector4(0.35f, 0.9f, 0.45f, 1f)
            : status.StartsWith("Error", StringComparison.OrdinalIgnoreCase) ? new Vector4(1f, 0.42f, 0.38f, 1f)
            : new Vector4(0.8f, 0.8f, 0.8f, 1f);
        ImGui.TextColored(color, status);
    }
}
