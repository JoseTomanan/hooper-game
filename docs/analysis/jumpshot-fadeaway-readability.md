# Jump-shot fadeaway readability follow-up

The normal JumpShot now turns at 30% of the configured heading rate during
Startup. The fixed rim target, back-turn factor, 18/4/20 timeline, release
classifier and facing-scatter formula remain unchanged. ADR-0010 records the
provisional rate. The fadeaway uses a separate landing selected only when the
ball actually accepts that move's release; the move parameter also carries the
release result to the observer and owner correction path.

## Production-input measurements

The capture fixture sets a starting heading and drives the production shot
input. For the new cases it leaves the scene's heading rate unmodified. Both
hands produced one release each. With a 900-degree/second base rate, a square
start released square (facing factor 1); a 90-degree start released at 14.66
degrees (factor 1.065) and selected the normal shot. Starts at ±170 degrees
released at 96.38 degrees (factor 1.428), and 180 degrees released at 106.59
degrees (factor 1.474), selecting the fadeaway. These factors quantify the
accuracy cost from the existing scatter model, not a feel verdict.

At matched Recovery frames 4 and 6, a production-input fadeaway had about
0.34–0.35 m difference in final foot height, settling to about 0.003 m by
frame 18. Replacing only the fadeaway Recovery with normal Recovery preserved
classification but failed the catch assertion. Both hands, both turn
directions, squared-up shots and accepted fakes are covered by
`tools/run_fadeaway_recovery_proof.py`.

[Normal-speed review clips](evidence/jumpshot-fadeaway-readability/README.md)
cover gameplay and side views, both hands, square controls and a separate ENet
observer. The old-turn comparison is a fixture reconstruction, not a capture
of an unchanged old checkout; the observer is paced. See the clip README.
Readability and taste remain for human review in #185/#173.

## Verification limits

The existing fadeaway form proof passes its old-asset comparison and the new
landing substitution control. The 70-degree authored per-tick local rotation
cap is a provisional continuity measure, not a claim that the motion feels
smooth. The final-skeleton boundary displacement check measured 1.057 m at
Startup/Active, 0.403 m at Active/Recovery and 0.035 m at Recovery/neutral;
the comparable prior asset measured 1.101 m, 0.680 m and 0.035 m. These are
large movements; the recordings and deferred human review remain important.

The 44-case JumpShotHeadingTest matrix passed, as did the 0 ms and 30 ms ENet
shot-heading scenarios. The latter observe successful fadeaway and fake
Recovery for owner and observer. Rejection and token-order repair were tested
with synthetic reconciliation messages; a live networked out-of-bounds or
possession-loss disagreement and a hold/reset specifically during Recovery are
not established by those tests.
