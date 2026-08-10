using System;
using System.Collections.Generic;
using System.Numerics;
using System.Linq;
using Cactjack.Game;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Game.Chat;

namespace Cactjack.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private static readonly Vector2 CardSize = new(58, 76);
    private readonly BlackjackGame game = new();
    private readonly TableSimulatorView tableSimulator = new();
    private readonly NetworkLobbyView networkLobby = new();
    private int nextWager = 25;
    private bool showTableSimulator;
    private bool showNetworkLobby;

    public MainWindow() : base("Cactjack###CactjackMainWindow")
    {
        Size = new Vector2(900, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(760, 500),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
        game.NewRound(nextWager);
    }

    public void Dispose()
    {
        tableSimulator.Dispose();
        networkLobby.Dispose();
    }

    public void HandlePartyChat(IChatMessage message) =>
        tableSimulator.HandlePartyChat(CleanPlayerName(message.OriginalSender.ExtractText()), message.OriginalMessage.ExtractText());

    private static string CleanPlayerName(string sender) =>
        new(sender.Where(character => char.IsLetter(character) || character is ' ' or '-' or '\'').ToArray());

    public override void Draw()
    {
        if (ImGui.Button("Solo Practice")) { showTableSimulator = false; showNetworkLobby = false; }
        ImGui.SameLine();
        if (ImGui.Button("Table Simulator")) { showTableSimulator = true; showNetworkLobby = false; }
        ImGui.SameLine();
        if (ImGui.Button("Network Lobby")) { showNetworkLobby = true; showTableSimulator = false; }
        ImGui.Spacing();
        if (showNetworkLobby)
        {
            networkLobby.Draw();
            return;
        }
        if (showTableSimulator)
        {
            tableSimulator.Draw();
            return;
        }

        var roundComplete = game.State == RoundState.Complete;
        ImGui.TextDisabled("CACTJACK  •  SIX-DECK TABLE");
        ImGui.SameLine();
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowWidth() - 315));
        ImGui.TextDisabled($"Chips {game.Chips:N0}   W {game.Wins}  L {game.Losses}  P {game.Pushes}");

        var hitSoft17 = game.DealerHitsSoft17;
        if (ImGui.Checkbox("Dealer hits soft 17", ref hitSoft17)) game.DealerHitsSoft17 = hitSoft17;
        ImGui.SameLine();
        ImGui.TextDisabled($"Shoe: {game.CardsRemaining} cards");
        ImGui.Spacing();

        ImGui.Text("DEALER");
        ImGui.Separator();
        ImGui.Spacing();
        if (roundComplete)
        {
            DrawHand(game.DealerHand, "dealer");
            ImGui.Text($"Total: {game.DealerScore}");
        }
        else
        {
            DrawCard(game.DealerHand[0], "dealer-0");
            ImGui.SameLine();
            DrawHiddenCard("dealer-hidden");
            ImGui.Text($"Showing: {BlackjackGame.Score(new[] { game.DealerHand[0] })}");
        }

        ImGui.Dummy(new Vector2(0, 18));
        for (var index = 0; index < game.PlayerHands.Count; index++)
            DrawPlayerHand(game.PlayerHands[index], index);

        ImGui.Separator();
        ImGui.Spacing();
        DrawStatus();
        ImGui.Spacing();

        if (!roundComplete) DrawActionButtons();
        else DrawBettingControls();
    }

    private void DrawPlayerHand(PlayerHand hand, int index)
    {
        var isActive = game.State == RoundState.PlayerTurn && index == game.ActiveHandIndex;
        ImGui.Text(isActive ? $"▶ HAND {index + 1}  •  WAGER {hand.Wager:N0}" : $"HAND {index + 1}  •  WAGER {hand.Wager:N0}");
        ImGui.Separator();
        ImGui.Spacing();
        DrawHand(hand.Cards, $"player-{index}");
        ImGui.Text($"Total: {hand.Score}");
        if (!string.IsNullOrEmpty(hand.Result))
            ImGui.TextColored(OutcomeColor(hand.Outcome), hand.Result);
        ImGui.Dummy(new Vector2(0, 12));
    }

    private void DrawActionButtons()
    {
        if (ImGui.Button("Hit", new Vector2(90, 34))) game.Hit();
        ImGui.SameLine();
        if (ImGui.Button("Stand", new Vector2(90, 34))) game.Stand();
        if (game.CanDoubleDown)
        {
            ImGui.SameLine();
            if (ImGui.Button("Double Down", new Vector2(120, 34))) game.DoubleDown();
        }
        if (game.CanSplit)
        {
            ImGui.SameLine();
            if (ImGui.Button("Split", new Vector2(90, 34))) game.Split();
        }
    }

    private void DrawBettingControls()
    {
        ImGui.SetNextItemWidth(180);
        ImGui.InputInt("Next wager", ref nextWager, 5, 25);
        nextWager = Math.Clamp(nextWager, 1, Math.Max(1, game.Chips));
        if (game.Chips > 0)
        {
            if (ImGui.Button("Deal New Hand", new Vector2(145, 34))) game.NewRound(nextWager);
            ImGui.SameLine();
            if (ImGui.Button("Bet Max", new Vector2(90, 34))) nextWager = game.Chips;
        }
        else
        {
            ImGui.TextColored(new Vector4(1f, 0.42f, 0.38f, 1f), "Out of chips.");
            if (ImGui.Button("Reset Bankroll", new Vector2(145, 34)))
            {
                game.ResetBankroll();
                nextWager = 25;
            }
        }
    }

    private void DrawStatus()
    {
        var outcome = game.PlayerHands.Count == 1 ? game.PlayerHands[0].Outcome : RoundOutcome.None;
        ImGui.TextColored(OutcomeColor(outcome), game.Status);
    }

    private static Vector4 OutcomeColor(RoundOutcome outcome) => outcome switch
    {
        RoundOutcome.Win => new Vector4(0.35f, 0.9f, 0.45f, 1f),
        RoundOutcome.Loss => new Vector4(1f, 0.42f, 0.38f, 1f),
        RoundOutcome.Push => new Vector4(0.95f, 0.78f, 0.3f, 1f),
        _ => new Vector4(0.8f, 0.8f, 0.8f, 1f)
    };

    private static void DrawHand(IReadOnlyList<Card> hand, string idPrefix)
    {
        for (var index = 0; index < hand.Count; index++)
        {
            if (index > 0) ImGui.SameLine();
            DrawCard(hand[index], $"{idPrefix}-{index}");
        }
    }

    private static void DrawCard(Card card, string id)
    {
        var text = card.IsRed ? new Vector4(0.72f, 0.08f, 0.08f, 1f) : new Vector4(0.08f, 0.08f, 0.1f, 1f);
        ImGui.PushStyleColor(ImGuiCol.Text, text);
        PushCardBackground(new Vector4(0.94f, 0.91f, 0.82f, 1f));
        ImGui.Button($"{card}##{id}", CardSize);
        ImGui.PopStyleColor(4);
    }

    private static void DrawHiddenCard(string id)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.95f, 0.85f, 0.45f, 1f));
        PushCardBackground(new Vector4(0.28f, 0.08f, 0.08f, 1f));
        ImGui.Button($"?##{id}", CardSize);
        ImGui.PopStyleColor(4);
    }

    private static void PushCardBackground(Vector4 color)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, color);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, color);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, color);
    }
}
