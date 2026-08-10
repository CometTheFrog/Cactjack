using System;
using System.Collections.Generic;

namespace Cactjack.Game;

public enum RoundState { PlayerTurn, Complete }
public enum RoundOutcome { None, Win, Loss, Push }
public enum Suit { Clubs, Diamonds, Hearts, Spades }
public enum Rank { Ace = 1, Two, Three, Four, Five, Six, Seven, Eight, Nine, Ten, Jack, Queen, King }

public readonly record struct Card(Rank Rank, Suit Suit)
{
    public bool IsRed => Suit is Suit.Diamonds or Suit.Hearts;
    public int SplitValue => Rank is Rank.Jack or Rank.Queen or Rank.King ? 10 : (int)Rank;

    public override string ToString()
    {
        var rank = Rank switch
        {
            Rank.Ace => "A",
            Rank.Jack => "J",
            Rank.Queen => "Q",
            Rank.King => "K",
            _ => ((int)Rank).ToString()
        };
        var suit = Suit switch
        {
            Suit.Clubs => "♣",
            Suit.Diamonds => "♦",
            Suit.Hearts => "♥",
            Suit.Spades => "♠",
            _ => "?"
        };
        return $"{rank}{suit}";
    }
}

public sealed class PlayerHand
{
    public List<Card> Cards { get; } = new();
    public int Wager { get; set; }
    public bool IsFinished { get; set; }
    public bool WasSplit { get; set; }
    public RoundOutcome Outcome { get; set; }
    public string Result { get; set; } = string.Empty;
    public int Score => BlackjackGame.Score(Cards);
    public bool IsBlackjack => !WasSplit && Cards.Count == 2 && Score == 21;
}

public sealed class BlackjackGame
{
    private const int DeckCount = 6;
    private const int CutCardRemaining = 78;
    private readonly Random random = new();
    private readonly List<Card> shoe = new();

    public List<PlayerHand> PlayerHands { get; } = new();
    public List<Card> DealerHand { get; } = new();
    public RoundState State { get; private set; } = RoundState.Complete;
    public int ActiveHandIndex { get; private set; }
    public int Chips { get; private set; } = 1000;
    public int Wins { get; private set; }
    public int Losses { get; private set; }
    public int Pushes { get; private set; }
    public bool DealerHitsSoft17 { get; set; }
    public string Status { get; private set; } = "Place a wager to begin.";
    public int DealerScore => Score(DealerHand);
    public int CardsRemaining => shoe.Count;
    public int HandsPlayed => Wins + Losses + Pushes;
    public PlayerHand ActiveHand => PlayerHands[ActiveHandIndex];
    public bool CanDoubleDown => State == RoundState.PlayerTurn && ActiveHand.Cards.Count == 2 && Chips >= ActiveHand.Wager;
    public bool CanSplit => State == RoundState.PlayerTurn && PlayerHands.Count < 4 && ActiveHand.Cards.Count == 2
                            && ActiveHand.Cards[0].SplitValue == ActiveHand.Cards[1].SplitValue && Chips >= ActiveHand.Wager;

    public BlackjackGame() => ShuffleShoe();

    public void ResetBankroll()
    {
        if (State == RoundState.Complete)
        {
            Chips = 1000;
            Status = "Bankroll reset to 1,000 chips.";
        }
    }

    public bool NewRound(int wager)
    {
        if (State == RoundState.PlayerTurn)
        {
            Status = "Finish the current hand first.";
            return false;
        }
        if (wager < 1 || wager > Chips)
        {
            Status = "Wager must be between 1 and your current chips.";
            return false;
        }
        if (shoe.Count <= CutCardRemaining)
            ShuffleShoe();

        Chips -= wager;
        PlayerHands.Clear();
        DealerHand.Clear();
        ActiveHandIndex = 0;

        var hand = new PlayerHand { Wager = wager };
        PlayerHands.Add(hand);
        hand.Cards.Add(DrawCard());
        DealerHand.Add(DrawCard());
        hand.Cards.Add(DrawCard());
        DealerHand.Add(DrawCard());
        State = RoundState.PlayerTurn;
        Status = "Your turn.";

        if (hand.IsBlackjack || IsBlackjack(DealerHand))
            FinishDealerAndSettle();
        return true;
    }

    public void Hit()
    {
        if (State != RoundState.PlayerTurn) return;
        ActiveHand.Cards.Add(DrawCard());
        if (ActiveHand.Score > 21)
        {
            ActiveHand.IsFinished = true;
            AdvanceHandOrDealer();
        }
        else if (ActiveHand.Score == 21)
        {
            Stand();
        }
    }

    public void Stand()
    {
        if (State != RoundState.PlayerTurn) return;
        ActiveHand.IsFinished = true;
        AdvanceHandOrDealer();
    }

    public void DoubleDown()
    {
        if (!CanDoubleDown) return;
        Chips -= ActiveHand.Wager;
        ActiveHand.Wager *= 2;
        ActiveHand.Cards.Add(DrawCard());
        ActiveHand.IsFinished = true;
        AdvanceHandOrDealer();
    }

    public void Split()
    {
        if (!CanSplit) return;
        var original = ActiveHand;
        Chips -= original.Wager;
        var splitHand = new PlayerHand { Wager = original.Wager, WasSplit = true };
        original.WasSplit = true;
        splitHand.Cards.Add(original.Cards[1]);
        original.Cards.RemoveAt(1);
        original.Cards.Add(DrawCard());
        splitHand.Cards.Add(DrawCard());
        PlayerHands.Insert(ActiveHandIndex + 1, splitHand);
        Status = $"Playing hand {ActiveHandIndex + 1} of {PlayerHands.Count}.";
    }

    public static int Score(IEnumerable<Card> cards)
    {
        var total = 0;
        var aces = 0;
        foreach (var card in cards)
        {
            if (card.Rank == Rank.Ace) { total += 11; aces++; }
            else total += card.Rank is Rank.Jack or Rank.Queen or Rank.King ? 10 : (int)card.Rank;
        }
        while (total > 21 && aces-- > 0) total -= 10;
        return total;
    }

    private static bool IsBlackjack(IReadOnlyCollection<Card> cards) => cards.Count == 2 && Score(cards) == 21;

    private void AdvanceHandOrDealer()
    {
        for (var index = ActiveHandIndex + 1; index < PlayerHands.Count; index++)
        {
            if (PlayerHands[index].IsFinished) continue;
            ActiveHandIndex = index;
            Status = $"Playing hand {ActiveHandIndex + 1} of {PlayerHands.Count}.";
            return;
        }
        FinishDealerAndSettle();
    }

    private void FinishDealerAndSettle()
    {
        var allPlayersBusted = PlayerHands.TrueForAll(hand => hand.Score > 21);
        while (!allPlayersBusted && (DealerScore < 17 || (DealerScore == 17 && DealerHitsSoft17 && IsSoft(DealerHand))))
            DealerHand.Add(DrawCard());

        foreach (var hand in PlayerHands)
            Settle(hand);

        State = RoundState.Complete;
        Status = PlayerHands.Count == 1 ? PlayerHands[0].Result : "Split round complete.";
    }

    private void Settle(PlayerHand hand)
    {
        var dealerBlackjack = IsBlackjack(DealerHand);
        if (hand.Score > 21)
            CompleteHand(hand, RoundOutcome.Loss, "Bust — dealer wins.", 0);
        else if (hand.IsBlackjack && dealerBlackjack)
            CompleteHand(hand, RoundOutcome.Push, "Both have blackjack — push.", hand.Wager);
        else if (hand.IsBlackjack)
            CompleteHand(hand, RoundOutcome.Win, "Blackjack! Paid 3:2.", hand.Wager + hand.Wager * 3 / 2);
        else if (dealerBlackjack)
            CompleteHand(hand, RoundOutcome.Loss, "Dealer has blackjack.", 0);
        else if (DealerScore > 21)
            CompleteHand(hand, RoundOutcome.Win, "Dealer busts — you win!", hand.Wager * 2);
        else if (hand.Score > DealerScore)
            CompleteHand(hand, RoundOutcome.Win, "You win!", hand.Wager * 2);
        else if (hand.Score < DealerScore)
            CompleteHand(hand, RoundOutcome.Loss, "Dealer wins.", 0);
        else
            CompleteHand(hand, RoundOutcome.Push, "Push.", hand.Wager);
    }

    private void CompleteHand(PlayerHand hand, RoundOutcome outcome, string result, int chipsReturned)
    {
        hand.IsFinished = true;
        hand.Outcome = outcome;
        hand.Result = result;
        Chips += chipsReturned;
        if (outcome == RoundOutcome.Win) Wins++;
        else if (outcome == RoundOutcome.Loss) Losses++;
        else if (outcome == RoundOutcome.Push) Pushes++;
    }

    private static bool IsSoft(IEnumerable<Card> cards)
    {
        var hardTotal = 0;
        var hasAce = false;
        foreach (var card in cards)
        {
            if (card.Rank == Rank.Ace) { hardTotal += 1; hasAce = true; }
            else hardTotal += card.Rank is Rank.Jack or Rank.Queen or Rank.King ? 10 : (int)card.Rank;
        }
        return hasAce && hardTotal + 10 <= 21;
    }

    private void ShuffleShoe()
    {
        shoe.Clear();
        for (var deck = 0; deck < DeckCount; deck++)
        foreach (var suit in Enum.GetValues<Suit>())
        foreach (var rank in Enum.GetValues<Rank>())
            shoe.Add(new Card(rank, suit));

        for (var index = shoe.Count - 1; index > 0; index--)
        {
            var swapIndex = random.Next(index + 1);
            (shoe[index], shoe[swapIndex]) = (shoe[swapIndex], shoe[index]);
        }
    }

    private Card DrawCard()
    {
        var index = shoe.Count - 1;
        var card = shoe[index];
        shoe.RemoveAt(index);
        return card;
    }
}
