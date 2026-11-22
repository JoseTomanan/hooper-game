# Fadeaway review clips

These 60 fps videos show the full plant, release and landing at normal
simulation speed. They are review evidence, not a feel verdict.

- [Gameplay, right hand](local-gameplay-right.mp4) and [left hand](local-gameplay-left.mp4)
- [Side view, right hand](local-side-right.mp4) and [left hand](local-side-left.mp4)
- [ENet observer, right hand](remote-side-right.mp4)
- [Squared-up controls, right](control-square-right.mp4) and [left](control-square-left.mp4)

The comparison uses the same 170-degree starting heading and real shot input.
The left panel reproduces the old 900 degrees/second nominal shot turn by
setting the current fixture's base turn rate to 3000 degrees/second before its
0.3 shot multiplier. This is an explicit fixture override, not an unmodified
old checkout. It releases at 0 degrees and selects the normal shot. The right
panel uses scene defaults, releases at approximately 96.38 degrees and selects
FadeawayActive and FadeawayRecovery. Squared-up controls use scene defaults.

The ENet observer is a genuine separate client with server-tick-matched
player, ball and skeleton samples; the producer is paced to retain every source
tick. The video plays those simulation frames at 60 fps; it is not a claim
about real-time network performance. The clips pair identical phase/frame
counters, showing uncropped source views resized side-by-side with labels.
The candidate animation resource SHA-256 is
`99c00edd4bf9301f0f0593e8e04201be4142687e8c953e08fe9bfa9dd2093a69`.
Human review of readability and motion remains in #185/#173.
