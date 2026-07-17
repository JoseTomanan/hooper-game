shader_type spatial;

// Why a shader instead of a texture: ADR-0020 (low-spec/old-gen budget) wants
// alpha-scissor/cutout transparency, not alpha-blend, for net/fence-style
// materials — cutout is cheaper on the Mobile renderer (ADR-0006) because
// opaque strands render in the depth prepass instead of needing sorted
// blending. We don't have an actual net/chain-link *texture* asset yet, so
// this procedurally punches diamond-shaped holes in UV space: two families of
// diagonal lines (UV.x + UV.y and UV.x - UV.y) crossing to form a lattice.
// Reused for both the basketball net (fine, white, cloth-like) and the
// chain-link fence (coarser, metallic) via the uniforms below — same pattern
// math, different look, so we don't duplicate near-identical shader files.
// cull_disabled is deliberate for the net (the far side of the cone shows
// through its own cutout holes, so it reads as a real hanging net) and is
// accepted, not overlooked, on the three wall BoxMeshes too: render_mode is
// per-shader, not per-material-use, so the walls pay the same double-sided
// fragment cost. That's a defensible trade here (back-face strands give the
// chain-link fence visible depth through its holes) under the ADR-0020
// low-spec budget; revisit only if profiling shows the walls fragment-bound.
render_mode cull_disabled, diffuse_burley, specular_schlick_ggx;

// vec3, not vec4: ALPHA is driven entirely by the strand pattern below, never
// by this uniform's alpha channel. The StandardMaterial3Ds this replaced DID
// use their albedo alpha to control transparency, so a vec4 source_color here
// (with an inspector alpha slider that silently does nothing) would be a
// trap for the next person tuning these materials.
uniform vec3 albedo_color : source_color = vec3(1.0);
// How many diamond repeats fit across the mesh's UV space on each axis.
uniform float uv_scale_u : hint_range(1.0, 64.0) = 24.0;
uniform float uv_scale_v : hint_range(1.0, 64.0) = 24.0;
// Fraction of each repeat that stays opaque (the "strand"/"wire"); the rest
// is discarded, i.e. the diamond-shaped hole you see through.
uniform float strand_width : hint_range(0.02, 0.5) = 0.12;
uniform float metallic_amount : hint_range(0.0, 1.0) = 0.0;
uniform float roughness_amount : hint_range(0.0, 1.0) = 0.6;

void fragment() {
	vec2 uv = UV * vec2(uv_scale_u, uv_scale_v);
	float diag_a = fract(uv.x + uv.y);
	float diag_b = fract(uv.x - uv.y);
	float strand = max(
		step(diag_a, strand_width) + step(1.0 - strand_width, diag_a),
		step(diag_b, strand_width) + step(1.0 - strand_width, diag_b)
	);

	ALBEDO = albedo_color;
	METALLIC = metallic_amount;
	ROUGHNESS = roughness_amount;
	// Cutout, not blend: pixels below the threshold are discarded outright
	// (ADR-0020), so the net/fence reads as genuinely see-through instead of
	// a translucent solid haze.
	ALPHA = clamp(strand, 0.0, 1.0);
	ALPHA_SCISSOR_THRESHOLD = 0.5;
}
