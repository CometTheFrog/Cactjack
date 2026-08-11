using System;
using System.Collections.Generic;
using Cactjack.Game;
using Cactjack.Networking;

namespace Cactjack.Windows;

internal sealed class TablePresentationTimeline
{
    private readonly Queue<TimelineStep> steps = new();
    private readonly Dictionary<int, int> visibleSeatCards = new();
    private DateTime nextStepAt;
    private DateTime activeStepStartedAt;
    private int activeStepDuration = 1;

    public PresentationMotionMode MotionMode { get; set; } = PresentationMotionMode.Normal;
    public bool IsAnimating => steps.Count > 0 || PendingCardIndex >= 0 || RevealingCardIndex >= 0;
    public int VisibleDealerCards { get; private set; }
    public bool ShowDealerHoleBack { get; private set; }
    public int PendingSeatIndex { get; private set; } = -1;
    public int PendingCardIndex { get; private set; } = -1;
    public int RevealingSeatIndex { get; private set; } = -1;
    public int RevealingCardIndex { get; private set; } = -1;
    public float StepProgress => Math.Clamp((float)(DateTime.UtcNow - activeStepStartedAt).TotalMilliseconds / activeStepDuration, 0f, 1f);

    public int VisibleCardsForSeat(int seatIndex) => visibleSeatCards.GetValueOrDefault(seatIndex);

    public void Apply(TableSnapshot? previous, TableSnapshot next)
    {
        steps.Clear();
        PendingSeatIndex = -1;
        PendingCardIndex = -1;
        RevealingSeatIndex = -1;
        RevealingCardIndex = -1;

        if (previous is null || next.Phase is TablePhase.Lobby or TablePhase.Betting || MotionMode == PresentationMotionMode.Reduced)
        {
            SetComplete(next);
            return;
        }

        SyncToPrevious(previous);

        if (previous.Phase is TablePhase.Lobby or TablePhase.Betting && next.Phase == TablePhase.PlayerTurns)
        {
            visibleSeatCards.Clear();
            VisibleDealerCards = 0;
            ShowDealerHoleBack = false;
            foreach (var seat in next.Seats)
                if (seat.IsOccupied && seat.Cards.Count > 0) QueueCard(seat.Number - 1, 0);
            if (next.VisibleDealerCards.Count > 0) QueueCard(-1, 0);
            foreach (var seat in next.Seats)
                if (seat.IsOccupied && seat.Cards.Count > 1) QueueCard(seat.Number - 1, 1);
            if (next.DealerHoleCardHidden)
                Enqueue(() => ShowDealerHoleBack = true, 260);
        }
        else
        {
            foreach (var seat in next.Seats)
            {
                var seatIndex = seat.Number - 1;
                for (var card = VisibleCardsForSeat(seatIndex); card < seat.Cards.Count; card++)
                    QueueCard(seatIndex, card);
            }

            if (previous.DealerHoleCardHidden && !next.DealerHoleCardHidden)
            {
                ShowDealerHoleBack = true;
                Enqueue(() => ShowDealerHoleBack = false, 80);
                for (var card = VisibleDealerCards; card < next.VisibleDealerCards.Count; card++)
                    QueueCard(-1, card, card == VisibleDealerCards ? 420 : 300);
            }
            else
            {
                for (var card = VisibleDealerCards; card < next.VisibleDealerCards.Count; card++)
                    QueueCard(-1, card);
                ShowDealerHoleBack = next.DealerHoleCardHidden;
            }
        }

        if (steps.Count == 0) SetComplete(next);
        else
        {
            activeStepStartedAt = DateTime.UtcNow;
            activeStepDuration = ScaleDelay(160);
            nextStepAt = activeStepStartedAt.AddMilliseconds(activeStepDuration);
        }
    }

    public void Update()
    {
        if (steps.Count == 0 || DateTime.UtcNow < nextStepAt) return;
        var step = steps.Dequeue();
        step.Action();
        activeStepStartedAt = DateTime.UtcNow;
        activeStepDuration = ScaleDelay(step.DelayMilliseconds);
        nextStepAt = activeStepStartedAt.AddMilliseconds(activeStepDuration);
    }

    private void QueueCard(int seatIndex, int cardIndex, int revealDelay = 240)
    {
        Enqueue(() =>
        {
            PendingSeatIndex = seatIndex;
            PendingCardIndex = cardIndex;
        }, 260);
        Enqueue(() =>
        {
            if (seatIndex < 0) VisibleDealerCards = Math.Max(VisibleDealerCards, cardIndex + 1);
            else visibleSeatCards[seatIndex] = Math.Max(VisibleCardsForSeat(seatIndex), cardIndex + 1);
            PendingSeatIndex = -1;
            PendingCardIndex = -1;
            RevealingSeatIndex = seatIndex;
            RevealingCardIndex = cardIndex;
        }, revealDelay);
        Enqueue(() =>
        {
            RevealingSeatIndex = -1;
            RevealingCardIndex = -1;
        }, 90);
    }

    private void Enqueue(Action action, int delayMilliseconds) => steps.Enqueue(new TimelineStep(action, delayMilliseconds));

    private int ScaleDelay(int milliseconds) => MotionMode == PresentationMotionMode.Fast
        ? Math.Max(45, (int)(milliseconds * 0.48f))
        : milliseconds;

    private void SyncToPrevious(TableSnapshot previous)
    {
        visibleSeatCards.Clear();
        foreach (var seat in previous.Seats)
            visibleSeatCards[seat.Number - 1] = seat.Cards.Count;
        VisibleDealerCards = previous.VisibleDealerCards.Count;
        ShowDealerHoleBack = previous.DealerHoleCardHidden;
    }

    private void SetComplete(TableSnapshot snapshot)
    {
        visibleSeatCards.Clear();
        foreach (var seat in snapshot.Seats)
            visibleSeatCards[seat.Number - 1] = seat.Cards.Count;
        VisibleDealerCards = snapshot.VisibleDealerCards.Count;
        ShowDealerHoleBack = snapshot.DealerHoleCardHidden;
        steps.Clear();
        PendingSeatIndex = -1;
        PendingCardIndex = -1;
        RevealingSeatIndex = -1;
        RevealingCardIndex = -1;
    }

    private sealed record TimelineStep(Action Action, int DelayMilliseconds);
}

internal enum PresentationMotionMode
{
    Normal,
    Fast,
    Reduced
}
