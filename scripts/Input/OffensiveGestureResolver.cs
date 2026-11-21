#nullable enable

using Hooper.Player;

namespace Hooper.Moves;

internal static class OffensiveGestureResolver
{
    internal static (CommittedMove? Move, float Param) Resolve(GestureResult gesture, bool sizeUp, bool finesse,
        HandSide hand, bool holdsBall, bool dribbling)
    {
        switch (gesture.Kind)
        {
            case GestureKind.Crossover:
            case GestureKind.QuickReturn:
                int sign = System.Math.Sign(gesture.Direction);
                if (!HandStateResolver.IsCrossover(hand, sign))
                    return (new Hesitation(), sign);
                if (gesture.Kind == GestureKind.QuickReturn)
                    return (new InAndOut(sign), sign);

                CommittedMove variation = (sizeUp, finesse) switch
                {
                    (true, true) => new Spin(sign),
                    (true, false) => new BehindTheBack(sign),
                    (false, true) => new BetweenTheLegs(sign),
                    _ => new Crossover(sign),
                };
                return (variation, sign);
            case GestureKind.StepBack when holdsBall:
                return (new StepBack(clientWasAlreadyDribbling: dribbling), 0f);
            case GestureKind.RetreatDribble when holdsBall && sizeUp:
                return (new RetreatDribble(), 0f);
            case GestureKind.RetreatDribble when holdsBall:
                int emptyHand = HandStateResolver.EmptyHandSign(hand);
                return (new BehindTheBack(emptyHand), emptyHand);
            default:
                return (null, 0f);
        }
    }
}
