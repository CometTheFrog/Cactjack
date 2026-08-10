using System;
using System.Numerics;
using Cactjack.Game;
using Cactjack.Networking;
using Dalamud.Bindings.ImGui;

namespace Cactjack.Windows;

public sealed class TableSimulatorView : IDisposable
{
    private static readonly Vector2 TableCardSize = new(46, 58);
    private readonly TableSession hostSession = new();
    private readonly TableHostProtocol hostProtocol;
    private readonly ITableTransport transport = new LocalLoopbackTransport();
    private TableSnapshot snapshot = null!;
    private int dummyNumber = 1;
    private int guestNumber = 1;
    private int viewIndex;

    public TableSimulatorView()
    {
        hostProtocol = new TableHostProtocol(hostSession);
        transport.CommandReceived += OnHostCommand;
        transport.SnapshotReceived += OnSnapshot;
        PublishView();
    }

    public void Dispose()
    {
        transport.CommandReceived -= OnHostCommand;
        transport.SnapshotReceived -= OnSnapshot;
        transport.Dispose();
    }

    public void HandlePartyChat(string sender, string message)
    {
        if (snapshot.Phase != TablePhase.PlayerTurns || snapshot.ActiveSeatIndex < 0) return;
        var guest = snapshot.Seats[snapshot.ActiveSeatIndex];
        if (!guest.IsGuest) return;
        var command = message.Trim().ToLowerInvariant();
        if (command is not ("cj hit" or "cj stand")) return;

        if (guest.Name.StartsWith("Guest ", StringComparison.OrdinalIgnoreCase))
            SendAsHost(TableCommandType.RenameSeat, guest.Number - 1, text: sender);
        else if (!string.Equals(guest.Name, sender, StringComparison.OrdinalIgnoreCase))
            return;

        SendAsHost(command == "cj hit" ? TableCommandType.Hit : TableCommandType.Stand);
    }

    public void Draw()
    {
        if (ImGui.Button($"View: {ViewLabel()}"))
        {
            viewIndex = (viewIndex + 1) % 5;
            PublishView();
        }
        ImGui.SameLine();
        ImGui.TextDisabled($"PROTOCOL v{snapshot.ProtocolVersion}  |  Rev {snapshot.Revision}  |  {snapshot.Phase}  |  Shoe {snapshot.CardsRemaining}");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.45f, 0.8f, 1f, 1f), $"Connected (loopback)  |  Code {snapshot.JoinCode}");
        ImGui.Separator();
        DrawSeats();

        if (snapshot.Phase is TablePhase.PlayerTurns or TablePhase.Settlement)
            DrawTable();

        ImGui.Spacing();
        if (snapshot.Phase is TablePhase.Lobby or TablePhase.Betting)
        {
            if (snapshot.CanManageSeats && snapshot.CanStart && ImGui.Button("Host: Deal Round", new Vector2(150, 34))) Send(TableCommandType.StartRound);
            else if (snapshot.CanManageSeats && !snapshot.CanStart) ImGui.TextDisabled("Every occupied seat must be ready with a valid wager.");
            else ImGui.TextDisabled("Waiting for the host to deal.");
        }
        else if (snapshot.Phase == TablePhase.PlayerTurns && snapshot.ActiveSeatIndex >= 0
                 && snapshot.Seats[snapshot.ActiveSeatIndex].IsLocal)
        {
            if (ImGui.Button("Hit", new Vector2(90, 34))) Send(TableCommandType.Hit);
            ImGui.SameLine();
            if (ImGui.Button("Stand", new Vector2(90, 34))) Send(TableCommandType.Stand);
        }
        else if (snapshot.Phase == TablePhase.PlayerTurns && snapshot.CanManageSeats && snapshot.ActiveSeatIndex >= 0
                 && snapshot.Seats[snapshot.ActiveSeatIndex].IsGuest)
        {
            var guest = snapshot.Seats[snapshot.ActiveSeatIndex];
            ImGui.TextColored(new Vector4(0.95f, 0.78f, 0.3f, 1f), $"Proxying {guest.Name}");
            if (ImGui.Button("Guest Hit", new Vector2(110, 34))) Send(TableCommandType.Hit);
            ImGui.SameLine();
            if (ImGui.Button("Guest Stand", new Vector2(110, 34))) Send(TableCommandType.Stand);
            ImGui.SameLine();
            if (ImGui.Button("Copy Party Update", new Vector2(150, 34))) ImGui.SetClipboardText(BuildPartyUpdate());
        }
        else if (snapshot.Phase == TablePhase.Settlement && snapshot.CanManageSeats)
        {
            if (ImGui.Button("Return to Betting", new Vector2(160, 34)))
                Send(TableCommandType.NextRound);
            ImGui.SameLine();
            if (ImGui.Button("Copy Party Results", new Vector2(165, 34)))
                ImGui.SetClipboardText(BuildPartyResults());
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Text("TABLE EVENT LOG");
        var start = Math.Max(0, snapshot.Events.Count - 8);
        for (var index = start; index < snapshot.Events.Count; index++) ImGui.TextDisabled(snapshot.Events[index]);
    }

    private void DrawSeats()
    {
        var flags = ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("BettingSeats", 5, flags)) return;

        ImGui.TableSetupColumn("Seat", ImGuiTableColumnFlags.WidthFixed, 55);
        ImGui.TableSetupColumn("Player / Bankroll", ImGuiTableColumnFlags.WidthStretch, 1);
        ImGui.TableSetupColumn("Wager", ImGuiTableColumnFlags.WidthFixed, 175);
        ImGui.TableSetupColumn("Ready", ImGuiTableColumnFlags.WidthFixed, 65);
        ImGui.TableSetupColumn("Host Actions", ImGuiTableColumnFlags.WidthFixed, 260);
        ImGui.TableHeadersRow();

        foreach (var seat in snapshot.Seats)
        {
            ImGui.PushID(seat.Number);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Text($"Seat {seat.Number}");
            ImGui.TableNextColumn();

            if (seat.IsOccupied)
            {
                var bankroll = seat.Chips >= 0 ? $"{seat.Chips:N0} chips" : "Bankroll private";
                ImGui.Text($"{seat.Name}{(seat.IsGuest ? " [Guest]" : string.Empty)}{(!seat.IsConnected ? " [Disconnected]" : string.Empty)}  |  {bankroll}");
                ImGui.TableNextColumn();
                if (snapshot.Phase is TablePhase.Lobby or TablePhase.Betting && (snapshot.CanManageSeats || seat.IsLocal))
                {
                    ImGui.SetNextItemWidth(-1);
                    var wager = seat.Wager;
                    if (ImGui.InputInt("##Wager", ref wager, 10_000, 100_000)) Send(TableCommandType.SetWager, seat.Number - 1, wager);
                    ImGui.TableNextColumn();
                    var ready = seat.IsReady;
                    if (ImGui.Checkbox("##Ready", ref ready)) Send(TableCommandType.SetReady, seat.Number - 1, ready ? 1 : 0);
                    ImGui.TableNextColumn();
                    if (snapshot.CanManageSeats && !seat.IsLocal)
                    {
                        if (ImGui.SmallButton(seat.IsConnected ? "Drop" : "Reconnect"))
                            Send(seat.IsConnected ? TableCommandType.DisconnectSeat : TableCommandType.ReconnectSeat, seat.Number - 1);
                        ImGui.SameLine();
                        if (ImGui.SmallButton("Kick")) Send(TableCommandType.KickSeat, seat.Number - 1);
                    }
                    else if (seat.IsLocal && !snapshot.CanManageSeats && ImGui.SmallButton("Leave"))
                        Send(TableCommandType.LeaveSeat, seat.Number - 1);
                }
                else
                {
                    ImGui.Text($"{seat.Wager:N0}");
                    ImGui.TableNextColumn();
                    ImGui.TextDisabled(seat.IsReady ? "Yes" : "No");
                    ImGui.TableNextColumn();
                }
            }
            else
            {
                ImGui.TextDisabled(seat.IsOpen ? "Open" : "Closed");
                ImGui.TableNextColumn();
                ImGui.TableNextColumn();
                ImGui.TableNextColumn();
                if (snapshot.CanManageSeats && snapshot.Phase is TablePhase.Lobby or TablePhase.Betting)
                {
                    if (seat.IsOpen && ImGui.SmallButton("+ Dummy"))
                        Send(TableCommandType.JoinSeat, seat.Number - 1, text: $"Dummy {dummyNumber++}");
                    ImGui.SameLine();
                    if (seat.IsOpen && ImGui.SmallButton("+ Guest"))
                        Send(TableCommandType.JoinSeat, seat.Number - 1, 1, $"Guest {guestNumber++}");
                    ImGui.SameLine();
                    if (seat.IsOpen && !System.Linq.Enumerable.Any(snapshot.Seats, existing => existing.IsLocal)
                        && ImGui.SmallButton("+ Me"))
                        Send(TableCommandType.JoinSeat, seat.Number - 1, 2);
                    ImGui.SameLine();
                    if (ImGui.SmallButton(seat.IsOpen ? "Close" : "Open"))
                        Send(TableCommandType.ToggleSeatOpen, seat.Number - 1);
                }
            }
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private void DrawTable()
    {
        ImGui.Spacing();
        var dealerTitleWidth = ImGui.CalcTextSize("DEALER").X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, (ImGui.GetContentRegionAvail().X - dealerTitleWidth) / 2));
        ImGui.Text("DEALER");
        ImGui.Separator();
        var dealerCardCount = snapshot.VisibleDealerCards.Count + (snapshot.DealerHoleCardHidden ? 1 : 0);
        var dealerCardsWidth = dealerCardCount * TableCardSize.X + Math.Max(0, dealerCardCount - 1) * 8;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, (ImGui.GetContentRegionAvail().X - dealerCardsWidth) / 2));
        DrawCards(snapshot.VisibleDealerCards, "dealer");
        if (snapshot.DealerHoleCardHidden)
        {
            ImGui.SameLine();
            DrawHiddenCard("dealer-hole");
        }
        var dealerTotal = snapshot.DealerHoleCardHidden
            ? $"Showing: {snapshot.VisibleDealerScore}"
            : $"Total: {snapshot.VisibleDealerScore}";
        var dealerTotalWidth = ImGui.CalcTextSize(dealerTotal).X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, (ImGui.GetContentRegionAvail().X - dealerTotalWidth) / 2));
        ImGui.Text(dealerTotal);

        ImGui.Dummy(new Vector2(0, 12));
        if (ImGui.BeginTable("PlayerSeats", 4, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchSame))
        {
            foreach (var seat in snapshot.Seats)
            {
                ImGui.TableNextColumn();
                if (!seat.IsOccupied)
                {
                    ImGui.TextDisabled($"SEAT {seat.Number}");
                    ImGui.TextDisabled(seat.IsOpen ? "Open" : "Closed");
                    continue;
                }

                var active = snapshot.ActiveSeatIndex == seat.Number - 1;
                ImGui.TextColored(active ? new Vector4(0.95f, 0.78f, 0.3f, 1f) : new Vector4(0.85f, 0.85f, 0.85f, 1f),
                    $"{(active ? "> " : string.Empty)}SEAT {seat.Number}: {seat.Name.ToUpperInvariant()}");
                var chips = seat.Chips >= 0 ? Compact(seat.Chips) : "Private";
                ImGui.TextDisabled($"Wager {Compact(seat.Wager)}  |  Chips {chips}");
                ImGui.Spacing();
                DrawCardsWrapped(seat.Cards, $"seat-{seat.Number}");
                ImGui.Text($"Total: {seat.Score}");
                if (!string.IsNullOrEmpty(seat.Result))
                    ImGui.TextColored(ResultColor(seat.Result), seat.Result);
                if (snapshot.CanManageSeats && !seat.IsLocal)
                {
                    if (ImGui.SmallButton($"{(seat.IsConnected ? "Disconnect" : "Reconnect")}##live-{seat.Number}"))
                        Send(seat.IsConnected ? TableCommandType.DisconnectSeat : TableCommandType.ReconnectSeat, seat.Number - 1);
                    ImGui.SameLine();
                    if (ImGui.SmallButton($"Kick##live-{seat.Number}")) Send(TableCommandType.KickSeat, seat.Number - 1);
                }
            }
            ImGui.EndTable();
        }
    }

    private void Send(TableCommandType type, int seat = -1, int value = 0, string text = "") =>
        transport.Send(new TableCommand(type, seat, value, text,
            snapshot.IsHostView ? "host" : snapshot.RecipientId,
            Guid.NewGuid().ToString("N"), snapshot.Revision));

    private void SendAsHost(TableCommandType type, int seat = -1, int value = 0, string text = "") =>
        transport.Send(new TableCommand(type, seat, value, text, "host",
            Guid.NewGuid().ToString("N"), snapshot.Revision));

    private void OnHostCommand(TableCommand command)
    {
        hostProtocol.Process(command);
        PublishView();
    }

    private void OnSnapshot(TableSnapshot nextSnapshot) => snapshot = nextSnapshot;

    private void PublishView()
    {
        var hostView = viewIndex == 0;
        var recipient = viewIndex switch
        {
            0 or 1 => "player-you",
            _ => $"dummy-{viewIndex - 1}"
        };
        transport.Publish(hostProtocol.CreateSnapshot(recipient, hostView));
    }

    private string ViewLabel() => viewIndex switch
    {
        0 => "Host",
        1 => "You",
        _ => $"Client {viewIndex - 1}"
    };

    private string BuildPartyUpdate()
    {
        var dealer = string.Join(" ", System.Linq.Enumerable.Select(snapshot.VisibleDealerCards, card => $"[{card}]"));
        if (snapshot.DealerHoleCardHidden) dealer += " [??]";
        if (snapshot.ActiveSeatIndex < 0) return $"Cactjack | Dealer: {dealer}";
        var active = snapshot.Seats[snapshot.ActiveSeatIndex];
        var cards = string.Join(" ", System.Linq.Enumerable.Select(active.Cards, card => $"[{card}]"));
        return $"Cactjack | Dealer: {dealer} | {active.Name}: {cards} = {active.Score} | Reply: cj hit or cj stand";
    }

    private string BuildPartyResults()
    {
        var parts = new System.Collections.Generic.List<string>
        {
            $"Cactjack Results | Dealer {snapshot.VisibleDealerScore}"
        };
        var hasGuests = System.Linq.Enumerable.Any(snapshot.Seats, seat => seat.IsOccupied && seat.IsGuest);
        foreach (var seat in snapshot.Seats)
        {
            if (!seat.IsOccupied || (hasGuests && !seat.IsGuest)) continue;
            var outcome = seat.Result.StartsWith("Won", StringComparison.OrdinalIgnoreCase) ? "WIN"
                : seat.Result.StartsWith("Push", StringComparison.OrdinalIgnoreCase) ? "PUSH"
                : "LOSS";
            parts.Add($"{seat.Name}: {outcome} ({seat.Score}) | Wager {seat.Wager:N0}");
        }
        return string.Join(" | ", parts);
    }

    private static void DrawCards(System.Collections.Generic.IReadOnlyList<Card> cards, string idPrefix)
    {
        for (var index = 0; index < cards.Count; index++)
        {
            if (index > 0) ImGui.SameLine();
            DrawCard(cards[index], $"{idPrefix}-{index}");
        }
    }

    private static void DrawCardsWrapped(System.Collections.Generic.IReadOnlyList<Card> cards, string idPrefix)
    {
        var cardsPerRow = Math.Max(1, (int)(ImGui.GetContentRegionAvail().X / (TableCardSize.X + 8)));
        for (var index = 0; index < cards.Count; index++)
        {
            if (index > 0 && index % cardsPerRow != 0) ImGui.SameLine();
            DrawCard(cards[index], $"{idPrefix}-{index}");
        }
    }

    private static void DrawCard(Card card, string id)
    {
        var textColor = card.IsRed
            ? new Vector4(0.72f, 0.08f, 0.08f, 1f)
            : new Vector4(0.08f, 0.08f, 0.1f, 1f);
        ImGui.PushStyleColor(ImGuiCol.Text, textColor);
        PushCardColors(new Vector4(0.94f, 0.91f, 0.82f, 1f));
        ImGui.Button($"{card}##{id}", TableCardSize);
        ImGui.PopStyleColor(4);
    }

    private static void DrawHiddenCard(string id)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.95f, 0.85f, 0.45f, 1f));
        PushCardColors(new Vector4(0.28f, 0.08f, 0.08f, 1f));
        ImGui.Button($"?##{id}", TableCardSize);
        ImGui.PopStyleColor(4);
    }

    private static void PushCardColors(Vector4 color)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, color);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, color);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, color);
    }

    private static Vector4 ResultColor(string result) => result.Contains("Won")
        ? new Vector4(0.35f, 0.9f, 0.45f, 1f)
        : result.Contains("Push") ? new Vector4(0.95f, 0.78f, 0.3f, 1f) : new Vector4(1f, 0.42f, 0.38f, 1f);

    private static string Compact(int value) => value switch
    {
        >= 1_000_000_000 => $"{value / 1_000_000_000d:0.##}B",
        >= 1_000_000 => $"{value / 1_000_000d:0.##}M",
        >= 1_000 => $"{value / 1_000d:0.##}K",
        _ => value.ToString("N0")
    };
}
