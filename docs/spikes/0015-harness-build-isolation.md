# Spike 0015 — integration-harness build isolation (#389)

**Status:** GO, bounded to a single Godot game assembly with an explicit
MSBuild compile-set switch. Ordinary CI compilation now passes
`IncludeIntegrationHarness=false`; every proof-producing build passes `true`.
The compatibility default remains `true` for Godot/IDE entry points that cannot
supply the property. Do not split integration scenes into a second C# assembly.

## Decision and public commands

The retained minimum prototype has two modes:

```powershell
# Fast ordinary game compile: no tests/integration/**/*.cs
dotnet build "HOOPER GAME.csproj" -p:IncludeIntegrationHarness=false

# Proof-capable game assembly: every current integration source included
dotnet build "HOOPER GAME.csproj" -p:IncludeIntegrationHarness=true
```

The property accepts exactly lowercase `true` or `false`; any other value fails
before package-asset resolution with a diagnostic naming the property. An
omitted property evaluates to `true`. That default is deliberate: existing
Godot editor, headless `--build-solutions`, export, and IDE builds remain
harness-capable, so forgetting the new property cannot silently weaken proof.

The build-and-test CI job explicitly selects `false`. Both jobs that load
integration scenes—the four headless catalog shards and rendered-evidence
capture—explicitly select `true`. Repository contract tests evaluate MSBuild's
live `Compile` items, compare harness mode with the filesystem inventory, pin
the CI commands, and force an invalid-property build with a missing NuGet assets
file to prove validation happens first.

## Why one conditional project, not an assembly split

Godot documents one generated solution/project for a C# game and requires that
assembly to be rebuilt before changed scripts can run. More decisively, Godot's
official issue tracker records that scripts compiled only into an additional
C# project cannot be instantiated by the engine because the runtime does not
find their associated classes. That makes a second harness assembly a larger
and riskier design, not the minimum experiment.

MSBuild already defines the needed primitive: `Compile` is an item collection,
item inclusion can carry a property condition, and `dotnet build -p:` supplies
the property on every supported host. The prototype therefore keeps Godot's
single assembly and conditionally re-includes the integration subtree after the
existing `tests/**/*.cs` removal.

Sources:

- [Godot 4.7 C# basics: generated solution/project and rebuild lifecycle](https://docs.godotengine.org/en/4.7/tutorials/scripting/c_sharp/c_sharp_basics.html)
- [Godot issue #77675: scripts from additional C# projects cannot be loaded](https://github.com/godotengine/godot/issues/77675)
- [MSBuild item inclusion/removal](https://learn.microsoft.com/en-us/visualstudio/msbuild/msbuild-items?view=visualstudio)
- [MSBuild conditions](https://learn.microsoft.com/en-us/visualstudio/msbuild/msbuild-conditions)
- [`dotnet build` forwards `-p:` properties to MSBuild](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-build)
- [MSBuild `-getItem` evaluation](https://learn.microsoft.com/en-us/visualstudio/msbuild/evaluate-items-and-properties?view=visualstudio)

## Compile-set characterization

Harness baseline revision: `985707ce4f289306c8d378e249263cea6b24e04d`.
Ordinary prototype revision: `34434ce`.

At the decision point, `tests/integration/` contains 79 C# files and 34,340
lines. The executable catalog contains 273 cases. MSBuild's evaluated item list
reports:

| Mode | Total `Compile` items | Integration items |
|---|---:|---:|
| omitted property (compatibility default) | 161 | 79 |
| `IncludeIntegrationHarness=true` | 161 | 79 |
| `IncludeIntegrationHarness=false` | 82 | 0 |

The harness-mode set equals the full recursive filesystem set—no missing,
duplicate, or stale hand-maintained list exists. Ordinary mode removes all 79.

The first ordinary compile exposed one real dependency leak: production
`GameManager` directly read a property declared by
`DedicatedScoreHarnessSeam.cs`. The fix retains the mutation state in the
harness partial and replaces the production read with an optional partial-void
hook. C# erases the declaration and call when the harness partial is absent, so
the 82-source ordinary assembly has no harness flag or input/config surface.
The dedicated score-RPC mutation scenario still exercises the implemented hook
in harness mode.

## Repeated timing measurement

Environment: Godot `4.7.1.stable.mono.official.a13da4feb`; Godot.NET.Sdk
`4.7.1`; .NET SDK `8.0.421`; MSBuild `17.11.48.46605`; Python `3.14.0`;
Windows `10.0.19045`; Intel64 Family 6 Model 165 Stepping 3.

Clean samples shut down all .NET build servers, ran `dotnet clean`, then timed
`dotnet build --no-restore -p:UseSharedCompilation=false -nodeReuse:false`.
The harness baseline omitted the new property on the baseline revision (whose
only compile set included all integration sources); ordinary samples added
`-p:IncludeIntegrationHarness=false` on the prototype revision. This removes
shared compiler/server contamination. No-op samples first warmed the matching
mode, then timed five incremental invocations with the normal shared compiler.

| Sample | harness clean (s) | ordinary clean (s) | harness no-op (s) | ordinary no-op (s) |
|---:|---:|---:|---:|---:|
| 1 | 5.388646 | 3.299413 | 0.837346 | 0.832002 |
| 2 | 5.357251 | 3.262502 | 0.837916 | 0.841237 |
| 3 | 5.545227 | 3.246403 | 0.843276 | 0.840406 |
| 4 | 5.527719 | 3.228996 | 0.842194 | 0.850268 |
| 5 | 5.437935 | 3.314111 | 0.881848 | 0.856554 |
| **mean** | **5.451356** | **3.270285** | **0.848516** | **0.844093** |
| **median** | **5.437935** | **3.262502** | **0.842194** | **0.841237** |

The ordinary clean build saves a mean 2.181071 seconds, or **40.0%**. No-op
savings are only 0.004423 seconds, or **0.5%**; the change is useful for clean
and source-changing agent/CI compiles, not repeated no-op commands.

## Godot 4.7.1 loading and failure semantics

This was verified through the engine, not inferred from MSBuild alone:

1. An ordinary `false` build succeeded with zero warnings/errors.
2. Starting the catalog smoke scene against that assembly made Godot emit
   `Cannot instantiate C# script because the associated class could not be
   found` for `IntegrationSmokeTest.cs`. The catalog returned nonzero
   `TIMEOUT`/124; it did not report PASS.
3. An explicit `true` rebuild succeeded, and the same catalog case passed 30
   deterministic fixed ticks under Godot 4.7.1.
4. An unsandboxed Godot `--headless --build-solutions --quit` run succeeded and
   registered all script classes using the compatibility default.

The timeout is a valid negative control, not the primary permanent guard. CI
selects the mode explicitly and stops on a failed build before starting the
catalog. Successful true↔false switches both invoked the compiler and refreshed
the shared Debug DLL. A failed build can leave a prior DLL on disk, as it could
before this spike, so callers must continue honoring the build exit code.

## Operational surface matrix

| Surface | Result / rule |
|---|---|
| Local Windows ordinary build | Explicit `false`; builds 82 sources. |
| Local/CI harness | Explicit `true`; builds all 161 sources before catalog execution. |
| Linux | Uses the same portable MSBuild condition; hosted PR CI is the authoritative Linux proof. |
| Godot editor and `--build-solutions` | Omit property and therefore retain the 161-source compatibility behavior. |
| IDE/design-time evaluation | Omitted property remains `true`; all scene scripts remain discoverable. |
| Export builds | Omitted property remains `true`; no export preset or target-platform behavior changes. |
| Generated C# UIDs/import cache | Source paths never move and no `.uid` file is edited; switching back to default/true lets Godot register the same classes. |
| Wrong property spelling | Build fails before restore/compile with a direct diagnostic. |
| Ordinary assembly used for proof | Integration root class cannot instantiate; catalog fails closed rather than passing vacuously. |

## Verification

The retained prototype passed:

- six repository contract tests for compile inventories, CI wiring, SDK order,
  and invalid configuration;
- ordinary and harness game builds with zero warnings/errors;
- 1,148 unit tests (five intentional characterization skips);
- Godot 4.7.1 smoke failure under the ordinary assembly and pass after the
  harness rebuild;
- all 260 single-process catalog cases under the harness assembly;
- all 13 multiprocess catalog cases through the supported Git Bash adapter,
  including `dedicated-game-journey-score-rpc-disabled`, which proves the
  relocated partial hook still suppresses the score RPC when armed;
- Godot 4.7.1 `--build-solutions` with the compatibility default.

Together those catalog runs cover all 273 registered cases. The first Windows
`--all` attempt resolved bare `bash` to a broken WSL installation: all 260
direct cases passed, while the 13 adapters failed before game execution with
`execvpe(/bin/bash) failed`. Re-running exactly the multiprocess partition with
`--bash "C:\Program Files\Git\bin\bash.exe"` outside the filesystem sandbox
produced `13 passed / 13 run`. This is an environment-resolution failure and
recovery, not a discarded gameplay red. Hosted Linux CI results are recorded on
the pull request that carries this spike.

## Verdict and bounded follow-up

**GO** for the property-based, single-assembly isolation and for using the
ordinary mode in the engine-free build-and-test job. The measured 40% clean
compile reduction justifies one property, one item condition, one early
validation target, and the small optional partial hook. Proof jobs remain
explicitly harness-inclusive and repository tests make source-set drift visible.

**NO-GO** for a second C# assembly, for making ordinary mode the implicit
default, or for teaching Godot/editor/export commands a custom build path. Those
choices add engine-loading and stale-artifact risk without additional measured
benefit.

A separate follow-up is warranted only if wrong-mode catalog runs become a real
operator cost. It may add a build-mode stamp and immediate catalog preflight,
but must bind the stamp to the actual assembly and fail closed on missing/stale
state. The current timeout negative control is safe but intentionally not
promoted into a broader production tool during this minimum spike.

No editor or feel verification is required. This is build/harness tooling; the
engine-facing checks are headless and state-verifiable.
