# #440 rendered comparisons

Each panel preserves the complete production frame, including the ball. The normal films show consecutive simulation source ticks at 60Hz (17/17/16ms frame durations); the GIFs show the same poses at 2.4x slow motion (40ms each). Paced network captures are retimed for this presentation and establish no latency or performance result.

Before and after retain their own actual clip positions and independent release events. The index includes original PNG SHA256 hashes, observed cameras, source ticks, playback offsets and compressed raw traces/manifests. Owning-client physics ticks are never joined to server event ticks.

The main films project the earliest evaluated observation per source tick. This projection omits the baseline's changed poses on held source ticks and must not be read as showing that the old playback was continuous. Every changed raw adjacent baseline edge appears in the linked diagnostic film at 500ms per original observation, paired with the repaired canonical pose at the same relative source tick held static. Those diagnostics label the local physics frames and actual playback state/time; their full original frame references and SHA256 hashes remain in the index. Only the named defect in the exact frozen baseline assets/scene is permitted; repaired captures reject it.

Appearance and smoothness remain unaccepted for the human pass in #173. The full visible ball/wrist relationship remains in the source images.

| Case | 60Hz source-tick projection | 2.4x slow projection | Contact sheet | Raw baseline held edges (500ms/frame) |
|---|---|---|---|---|
| local-side-left-dribble-normal-o0-1 | [WebP](local-side-left-dribble-normal-o0-1-normal.webp) | [GIF](local-side-left-dribble-normal-o0-1-slow.gif) | [PNG](local-side-left-dribble-normal-o0-1-contact.png) | none observed |
| local-side-right-dribble-normal-o0-1 | [WebP](local-side-right-dribble-normal-o0-1-normal.webp) | [GIF](local-side-right-dribble-normal-o0-1-slow.gif) | [PNG](local-side-right-dribble-normal-o0-1-contact.png) | none observed |

[Source frame and clock index](frame-index.json)
