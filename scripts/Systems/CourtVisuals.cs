using Godot;
using Hooper.Ball;

namespace Hooper.Systems;

/// <summary>
/// Procedural in-court visual indicators (issue #46): a clear-line arc and a
/// court-bound outline, both built in code so no manual mesh authoring or
/// wiring is needed beyond placing this node in the scene (see Main.tscn and
/// EDITOR_TASKS.md — the same approach BlobShadow uses in Ball.tscn).
///
/// ── Clear-line arc ───────────────────────────────────────────────────────
/// A flat torus ring on the court floor at BallController.ClearLineDistance
/// from the hoop (RimCenter XZ), coloured red when the current possession is
/// not yet cleared and green once it is.  Hidden (neutral) while no one holds
/// the ball — the clear state is only meaningful during a live possession.
/// Legibility requirement from ADR-0008: the arc shows BOTH where the line is
/// AND whether this possession has crossed it, so the player never has to guess
/// why a shot didn't count.
///
/// ── Court-bound outline ───────────────────────────────────────────────────
/// Four thin box segments tracing the CourtMin/CourtMax rectangle so the
/// player can see the half-court limit the ball clamp (and the editor-placed
/// StaticBody3D walls) enforce.  Static — built once and never changes colour.
///
/// ── Single source of truth ────────────────────────────────────────────────
/// Both indicators read their geometry from BallController's exported fields
/// (ClearLineDistance, RimCenter, CourtMin, CourtMax) via the "ball" group —
/// exactly the approach PossessionHud uses.  If you retune any of those
/// values in the Inspector, this node reflects them on the next scene load
/// without any manual update here.
/// </summary>
public partial class CourtVisuals : Node3D
{
	// ── Tunables ──────────────────────────────────────────────────────────

	/// <summary>Floor-plane Y (height above court surface) for the arc and outline meshes.</summary>
	[Export] public float IndicatorHeight { get; set; } = 0.01f;

	/// <summary>Ring thickness (torus inner→outer radius difference) for the clear-line arc.</summary>
	[Export] public float ArcThickness { get; set; } = 0.05f;

	/// <summary>Height of the court-bound outline boxes (metres above the floor).</summary>
	[Export] public float BoundLineHeight { get; set; } = 0.02f;

	/// <summary>Half-width of the court-bound outline boxes (metres).</summary>
	[Export] public float BoundLineThickness { get; set; } = 0.04f;

	// ── Colours ───────────────────────────────────────────────────────────

	private static readonly Color ColourCleared    = new(0.2f, 0.85f, 0.2f, 0.7f);  // green
	private static readonly Color ColourTakeItBack = new(0.85f, 0.1f, 0.1f, 0.7f);  // red
	private static readonly Color ColourBoundLine  = new(0.9f, 0.9f, 0.9f, 0.4f);   // faint white

	// ── Runtime state ─────────────────────────────────────────────────────

	private BallController _ball;
	private StandardMaterial3D _arcMaterial;
	private MeshInstance3D _arcMesh;

	// ── Lifecycle ─────────────────────────────────────────────────────────

	public override void _Ready()
	{
		_ball = GetTree().GetFirstNodeInGroup("ball") as BallController;
		if (_ball == null)
		{
			GD.PrintErr("[CourtVisuals] No node in 'ball' group found; indicators will not render until BallController is in the scene.");
			return;
		}

		BuildClearLineArc();
		BuildCourtBoundOutline();

		// Push-driven refresh: only update on possession / clear changes.
		_ball.PossessionChanged += OnPossessionChanged;

		// Render the opening state before the first event fires.
		OnPossessionChanged(_ball.StateMachine.HolderPeerId, _ball.IsCleared);
	}

	public override void _ExitTree()
	{
		if (_ball != null)
			_ball.PossessionChanged -= OnPossessionChanged;
	}

	// ── Builder helpers ───────────────────────────────────────────────────

	/// <summary>
	/// Builds the flat torus ring at the clear-line radius, positioned on the
	/// floor plane at the hoop's XZ centre.  The ring's height is controlled by
	/// IndicatorHeight above the floor (y=0).
	/// </summary>
	private void BuildClearLineArc()
	{
		float r = _ball.ClearLineDistance;
		float hoopX = _ball.RimCenter.X;
		float hoopZ = _ball.RimCenter.Z;

		_arcMaterial = new StandardMaterial3D
		{
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
			ShadingMode   = BaseMaterial3D.ShadingModeEnum.Unshaded,
			AlbedoColor   = ColourTakeItBack,
			CullMode      = BaseMaterial3D.CullModeEnum.Disabled,
		};

		var mesh = new TorusMesh
		{
			InnerRadius = r - ArcThickness * 0.5f,
			OuterRadius = r + ArcThickness * 0.5f,
			Rings        = 64,
			RingSegments = 8,
		};

		_arcMesh = new MeshInstance3D
		{
			Mesh      = mesh,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		};
		_arcMesh.SetSurfaceOverrideMaterial(0, _arcMaterial);
		// Flat torus: rotate 90° around X so the ring lies on the floor plane.
		_arcMesh.Transform = new Transform3D(
			Basis.FromEuler(new Vector3(Mathf.Pi * 0.5f, 0f, 0f)),
			new Vector3(hoopX, IndicatorHeight, hoopZ));

		AddChild(_arcMesh);
	}

	/// <summary>
	/// Builds four thin box-mesh segments tracing the CourtMin/CourtMax rectangle.
	/// </summary>
	private void BuildCourtBoundOutline()
	{
		var min = _ball.CourtMin;
		var max = _ball.CourtMax;

		float w = max.X - min.X;  // court width  (X axis)
		float d = max.Y - min.Y;  // court depth  (Z axis)
		float cx = (min.X + max.X) * 0.5f;
		float cz = (min.Y + max.Y) * 0.5f;
		float t  = BoundLineThickness;
		float h  = BoundLineHeight;
		float y  = IndicatorHeight + h * 0.5f;

		// Near wall (min Z), far wall (max Z), left wall (min X), right wall (max X).
		var segments = new (Vector3 size, Vector3 pos)[]
		{
			(new Vector3(w + t * 2f, h, t), new Vector3(cx,    y, min.Y)),
			(new Vector3(w + t * 2f, h, t), new Vector3(cx,    y, max.Y)),
			(new Vector3(t, h, d),           new Vector3(min.X, y, cz)),
			(new Vector3(t, h, d),           new Vector3(max.X, y, cz)),
		};

		var mat = new StandardMaterial3D
		{
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
			ShadingMode   = BaseMaterial3D.ShadingModeEnum.Unshaded,
			AlbedoColor   = ColourBoundLine,
			CullMode      = BaseMaterial3D.CullModeEnum.Disabled,
		};

		foreach (var (size, pos) in segments)
		{
			var seg = new MeshInstance3D
			{
				Mesh       = new BoxMesh { Size = size },
				CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
				Position   = pos,
			};
			seg.SetSurfaceOverrideMaterial(0, mat);
			AddChild(seg);
		}
	}

	// ── Signal handler ────────────────────────────────────────────────────

	/// <summary>
	/// Updates the clear-line arc colour on possession / clear state changes.
	/// Mirrors PossessionHud.Refresh's logic: holderPeerId 0 means the ball is
	/// loose (arc hidden); otherwise the arc is red (uncleared) or green (cleared).
	/// </summary>
	private void OnPossessionChanged(int holderPeerId, bool cleared)
	{
		if (_arcMaterial == null) return;

		if (holderPeerId == 0)
		{
			// Loose ball — hide the arc; cleared state is meaningless until
			// the next possession is awarded.
			_arcMesh.Visible = false;
			return;
		}

		_arcMesh.Visible      = true;
		_arcMaterial.AlbedoColor = cleared ? ColourCleared : ColourTakeItBack;
	}
}
