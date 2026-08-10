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
        ImGui.Text($"Room: {roomCode}  |  {(isHost ? "Host / Dealer" : playerName)}");
        ImGui.SameLine();
        if (ImGui.SmallButton("Copy Code")) ImGui.SetClipboardText(roomCode);
        ImGui.SameLine();
        if (ImGui.SmallButton("Disconnect")) Disconnect();
        ImGui.SameLine();
        DrawStatus();

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
            if (snapshot.Phase == TablePhase.PlayerTurns && snapshot.ViewerCanAct)
            {
                if (ImGui.Button("Hit", new Vector2(90, 34))) SendPlayerCommand(TableCommandType.Hit);
                ImGui.SameLine();
                if (ImGui.Button("Stand", new Vector2(90, 34))) SendPlayerCommand(TableCommandType.Stand);
            }
            if (isHost && snapshot.Phase == TablePhase.Settlement && ImGui.Button("Return to Betting", new Vector2(160, 34)))
                SendHostCommand(TableCommandType.NextRound);
        }
    }

    private void DrawHands()
    {
        var dealer = string.Join(" ", snapshot!.VisibleDealerCards.Select(card => $"[{card}]"));
        if (snapshot.DealerHoleCardHidden) dealer += " [??]";
        ImGui.Spacing();
        ImGui.Text($"Dealer: {dealer}  Total {snapshot.VisibleDealerScore}");
        foreach (var seat in snapshot.Seats)
        {
            if (!seat.IsOccupied || seat.Cards.Count == 0) continue;
            var cards = string.Join(" ", seat.Cards.Select(card => $"[{card}]"));
            var active = snapshot.ActiveSeatIndex == seat.Number - 1 ? "> " : string.Empty;
            ImGui.Text($"{active}{seat.Name}: {cards}  Total {seat.Score}  {seat.Result}");
        }
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
            snapshot = hostProtocol.CreateSnapshot("host", true);
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
        snapshot = hostProtocol.CreateSnapshot("host", true);
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

    private void OnSnapshot(TableSnapshot next) => snapshot = next;
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
