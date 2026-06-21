using Godot;
using Hooper.Ball;

namespace Hooper.Systems;

/// <summary>
/// Procedural in-court visual indicators (issue #46): a clear-line arc and a
/// court-bound outline, both built in code so no manual mesh authoring or
/// wiring is needed beyond placing this node in the scene (see Main.tscn and
/// EDITOR_TASKS.md — the same approach BlobShadow uses in Ball.tscn).
///
/// ── Clear-line arc (3-point line shape) ─────────────────────────────────
/// A flat painted ribbon on the court floor shaped like a basketball 3-point
/// line: a sweeping arc from right sideline to left sideline through the top
/// of the key (the in-court portion of the ClearLineDistance circle), plus two
/// straight "corner three" segments running from each arc endpoint down to the
/// baseline (CourtMin.Y).  Built with SurfaceTool triangle strips so the result
/// is a genuine flat mesh at floor level, not the raised 3D tube that TorusMesh
/// produces.
///
/// Colour: white (court paint) by default; glows red when a possession is live
/// and the holder has not yet cleared it — the "take it back" signal.  The line
/// is always visible; only the colour changes.
///
/// Legibility trade-off (ADR-0008): the clear rule is a pure radius
/// (ClearLine.IsBehindClearLine), so the corner segments technically represent
/// a zone the rule does not grant — a player behind the corner paint can still
/// be &lt;ClearLineDistance from the hoop and uncleared.  The author accepted
/// this as a realism trade-off (see plan docs).  If the misleading colour ever
/// becomes a problem, the corners can be kept permanently white while only the
/// arc colour changes.
///
/// ── Court-bound outline ───────────────────────────────────────────────────
/// Four thin box segments tracing the CourtMin/CourtMax rectangle so the
/// player can see the half-court limit the ball clamp (and the editor-placed
/// StaticBody3D walls) enforce.  Static — built once, never changes colour.
///
/// ── Single source of truth ────────────────────────────────────────────────
/// Both indicators read their geometry from BallController's exported fields
/// (ClearLineDistance, RimCenter, CourtMin, CourtMax) via the "ball" group —
/// exactly the approach PossessionHud uses.  Retune those values in the
/// Inspector and the indicators update automatically on the next scene load.
/// </summary>
public partial class CourtVisuals : Node3D
{
	// ── Tunables ──────────────────────────────────────────────────────────

	/// <summary>Floor-plane Y (metres above court surface) for all floor indicators.</summary>
	[Export] public float IndicatorHeight { get; set; } = 0.01f;

	/// <summary>Painted-line width for the clear-line arc and corner segments (metres).</summary>
	[Export] public float LineWidth { get; set; } = 0.05f;

	/// <summary>Height of the court-bound outline boxes (metres).</summary>
	[Export] public float BoundLineHeight { get; set; } = 0.02f;

	/// <summary>Thickness of the court-bound outline boxes (metres).</summary>
	[Export] public float BoundLineThickness { get; set; } = 0.04f;

	// ── Colours ───────────────────────────────────────────────────────────

	private static readonly Color ColourBase       = new(1.00f, 1.00f, 1.00f, 0.85f); // white paint
	private static readonly Color ColourTakeItBack = new(1.00f, 0.12f, 0.05f, 0.95f); // red glow
	private static readonly Color ColourBoundLine  = new(0.90f, 0.90f, 0.90f, 0.40f); // faint white

	// ── Runtime state ─────────────────────────────────────────────────────

	private BallController     _ball;
	private StandardMaterial3D _arcMaterial;
	private MeshInstance3D     _arcMesh;

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

		// Seed the colour before the first event fires.
		OnPossessionChanged(_ball.StateMachine.HolderPeerId, _ball.IsCleared);
	}

	public override void _ExitTree()
	{
		if (_ball != null)
			_ball.PossessionChanged -= OnPossessionChanged;
	}

	// ── Builder helpers ───────────────────────────────────────────────────

	/// <summary>
	/// Builds a flat painted ribbon shaped like a basketball 3-point line.
	///
	/// Geometry overview:
	///   θ is measured from the +X axis in the XZ floor plane; θ=π/2 → +Z (mid-court).
	///   The arc sweeps from sideAngle (≈32.7°) through π/2 to π−sideAngle (≈147.3°),
	///   where sideAngle = acos(halfCourtWidth / ClearLineDistance) is the angle at which
	///   the circle meets the sidelines.  Each arc step is two flat triangles (a thin quad)
	///   with CCW-from-above winding so GenerateNormals produces +Y.
	///
	///   The corner segments hang straight from each arc endpoint to CourtMin.Y (the
	///   baseline / near-court edge).  Their start vertices are computed with the same
	///   formula as the arc's last/first vertices, guaranteeing a seamless junction.
	///
	///   CullMode.Disabled makes the ribbon two-sided; GenerateNormals computes face
	///   normals as a correctness baseline for any future lit-material switch.
	/// </summary>
	private void BuildClearLineArc()
	{
		float r    = _ball.ClearLineDistance;  // 5.8 m (NBA ≈ top-of-key radius)
		float rimX = _ball.RimCenter.X;        // 0
		float rimZ = _ball.RimCenter.Z;        // 0.3
		float y    = IndicatorHeight;
		float half = LineWidth * 0.5f;

		// sideAngle: where the clear circle meets the sideline (CourtMax.X from rim centre).
		// The arc sweeps from this angle to its mirror through π/2 (mid-court direction).
		float halfW     = Mathf.Min(_ball.CourtMax.X - rimX, rimX - _ball.CourtMin.X);
		float sideAngle = Mathf.Acos(Mathf.Clamp(halfW / r, 0f, 1f));
		float startA    = sideAngle;                // right end, ≈ 32.7°
		// endA = π − sideAngle is the symmetric mirror of startA about π/2 (+Z).
		// This is exact when rimX = 0 (rim on the court centre-line, as in Main.tscn).
		// If rimX is ever non-zero, recompute the left sideAngle separately.
		float endA      = Mathf.Pi - sideAngle;     // left  end, ≈ 147.3°

		const int ArcSegs = 64;

		var st = new SurfaceTool();
		st.Begin(Mesh.PrimitiveType.Triangles);

		// ── Arc ───────────────────────────────────────────────────────────────
		// CCW-from-above winding: outer0, inner0, inner1 / outer0, inner1, outer1.
		// (Verified: cross-product gives +Y normal for this quad orientation.)

		for (int i = 0; i < ArcSegs; i++)
		{
			float a0 = Mathf.Lerp(startA, endA, (float)i       / ArcSegs);
			float a1 = Mathf.Lerp(startA, endA, (float)(i + 1) / ArcSegs);

			var inner0 = new Vector3(rimX + (r - half) * Mathf.Cos(a0), y, rimZ + (r - half) * Mathf.Sin(a0));
			var outer0 = new Vector3(rimX + (r + half) * Mathf.Cos(a0), y, rimZ + (r + half) * Mathf.Sin(a0));
			var inner1 = new Vector3(rimX + (r - half) * Mathf.Cos(a1), y, rimZ + (r - half) * Mathf.Sin(a1));
			var outer1 = new Vector3(rimX + (r + half) * Mathf.Cos(a1), y, rimZ + (r + half) * Mathf.Sin(a1));

			st.AddVertex(outer0); st.AddVertex(inner0); st.AddVertex(inner1);
			st.AddVertex(outer0); st.AddVertex(inner1); st.AddVertex(outer1);
		}

		// ── Corner segments ───────────────────────────────────────────────────
		// Arc endpoints at θ = startA (right) and θ = endA (left).
		// Each corner is a quad from the arc endpoint down to CourtMin.Y (baseline).
		// The start vertices are computed with the same formula as the arc's first/last
		// step, so the junction is seamless (shared edge, no gap or overlap).
		//
		// CCW-from-above winding (sort by world-space X ascending, then use BL→TL→TR / BL→TR→BR):
		//   Right corner: cos(startA) > 0  → inner (r−half) is at smaller X → innerBot, innerTop, outerTop
		//   Left  corner: cos(endA)   < 0  → outer (r+half) is at smaller X → outerBot, outerTop, innerTop

		// CourtMin is Vector2 where .Y stores world Z (the near-court Z edge).
		// See BallController's CourtMin XML comment: "Y = near edge (smallest Z)".
		float baseline = _ball.CourtMin.Y;   // -1.0 — world Z of the near-court edge

		// Right corner — arc endpoint at θ = startA
		var rcInnerTop = new Vector3(rimX + (r - half) * Mathf.Cos(startA), y, rimZ + (r - half) * Mathf.Sin(startA));
		var rcOuterTop = new Vector3(rimX + (r + half) * Mathf.Cos(startA), y, rimZ + (r + half) * Mathf.Sin(startA));
		var rcInnerBot = new Vector3(rcInnerTop.X, y, baseline);
		var rcOuterBot = new Vector3(rcOuterTop.X, y, baseline);
		st.AddVertex(rcInnerBot); st.AddVertex(rcInnerTop); st.AddVertex(rcOuterTop);
		st.AddVertex(rcInnerBot); st.AddVertex(rcOuterTop); st.AddVertex(rcOuterBot);

		// Left corner — arc endpoint at θ = endA
		var lcInnerTop = new Vector3(rimX + (r - half) * Mathf.Cos(endA), y, rimZ + (r - half) * Mathf.Sin(endA));
		var lcOuterTop = new Vector3(rimX + (r + half) * Mathf.Cos(endA), y, rimZ + (r + half) * Mathf.Sin(endA));
		var lcInnerBot = new Vector3(lcInnerTop.X, y, baseline);
		var lcOuterBot = new Vector3(lcOuterTop.X, y, baseline);
		st.AddVertex(lcOuterBot); st.AddVertex(lcOuterTop); st.AddVertex(lcInnerTop);
		st.AddVertex(lcOuterBot); st.AddVertex(lcInnerTop); st.AddVertex(lcInnerBot);

		st.GenerateNormals();

		_arcMaterial = new StandardMaterial3D
		{
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
			ShadingMode  = BaseMaterial3D.ShadingModeEnum.Unshaded,
			AlbedoColor  = ColourBase,
			CullMode     = BaseMaterial3D.CullModeEnum.Disabled,
		};

		_arcMesh = new MeshInstance3D
		{
			Mesh       = st.Commit(),
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		};
		_arcMesh.SetSurfaceOverrideMaterial(0, _arcMaterial);
		AddChild(_arcMesh);
	}

	/// <summary>
	/// Builds four thin box-mesh segments tracing the CourtMin/CourtMax rectangle.
	/// </summary>
	private void BuildCourtBoundOutline()
	{
		var min = _ball.CourtMin;
		var max = _ball.CourtMax;

		float w  = max.X - min.X;  // court width  (X axis)
		float d  = max.Y - min.Y;  // court depth  (Z axis, stored in Vector2.Y)
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
			ShadingMode  = BaseMaterial3D.ShadingModeEnum.Unshaded,
			AlbedoColor  = ColourBoundLine,
			CullMode     = BaseMaterial3D.CullModeEnum.Disabled,
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
	/// Recolours the clear-line arc on possession / clear state changes.
	/// The line is always visible (it is floor paint, not a HUD overlay);
	/// only its colour changes:
	///   live possession, not yet cleared → red  ("take it back")
	///   loose or already cleared         → white (neutral court paint)
	/// </summary>
	private void OnPossessionChanged(int holderPeerId, bool cleared)
	{
		if (_arcMaterial == null) return;

		// Red only when someone is actively holding the ball and hasn't cleared.
		bool needsClearing = holderPeerId != 0 && !cleared;
		_arcMaterial.AlbedoColor = needsClearing ? ColourTakeItBack : ColourBase;
	}
}
