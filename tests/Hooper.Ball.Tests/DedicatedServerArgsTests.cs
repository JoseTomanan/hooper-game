using Hooper.Networking;

namespace Hooper.Ball.Tests;

/// <summary>
/// Tests the pure dedicated-server launch-flag parsing (ADR-0007). This is the
/// one piece of the headless entry path provable without the Godot editor — the
/// node wiring and the actual headless run are hitl (EDITOR_TASKS M6).
/// </summary>
public class DedicatedServerArgsTests
{
	// ── IsDedicated ───────────────────────────────────────────────────────────

	[Fact]
	public void IsDedicated_true_when_flag_present()
	{
		Assert.True(DedicatedServerArgs.IsDedicated(new[] { "--dedicated" }));
		Assert.True(DedicatedServerArgs.IsDedicated(new[] { "--port", "7777", "--dedicated" }));
	}

	[Fact]
	public void IsDedicated_false_when_flag_absent()
	{
		Assert.False(DedicatedServerArgs.IsDedicated(new[] { "--port", "7777" }));
		Assert.False(DedicatedServerArgs.IsDedicated(System.Array.Empty<string>()));
	}

	[Fact]
	public void IsDedicated_false_for_null()
	{
		Assert.False(DedicatedServerArgs.IsDedicated(null!));
	}

	// ── ParsePort: valid forms ────────────────────────────────────────────────

	[Fact]
	public void ParsePort_reads_joined_form()
	{
		Assert.Equal(7777, DedicatedServerArgs.ParsePort(new[] { "--dedicated", "--port=7777" }, 1234));
	}

	[Fact]
	public void ParsePort_reads_separate_token_form()
	{
		Assert.Equal(7777, DedicatedServerArgs.ParsePort(new[] { "--dedicated", "--port", "7777" }, 1234));
	}

	// ── ParsePort: fallback cases ─────────────────────────────────────────────

	[Fact]
	public void ParsePort_falls_back_when_flag_absent()
	{
		Assert.Equal(1234, DedicatedServerArgs.ParsePort(new[] { "--dedicated" }, 1234));
	}

	[Fact]
	public void ParsePort_falls_back_when_value_missing()
	{
		// "--port" is the last token, no value follows.
		Assert.Equal(1234, DedicatedServerArgs.ParsePort(new[] { "--dedicated", "--port" }, 1234));
	}

	[Theory]
	[InlineData("--port=abc")]   // non-numeric
	[InlineData("--port=0")]     // below valid range
	[InlineData("--port=65536")] // above valid range
	[InlineData("--port=-1")]    // negative
	public void ParsePort_falls_back_on_invalid_value(string arg)
	{
		Assert.Equal(1234, DedicatedServerArgs.ParsePort(new[] { arg }, 1234));
	}

	[Fact]
	public void ParsePort_first_occurrence_wins()
	{
		Assert.Equal(7777, DedicatedServerArgs.ParsePort(
			new[] { "--port=7777", "--port=8080" }, 1234));
	}

	[Fact]
	public void ParsePort_null_returns_fallback()
	{
		Assert.Equal(1234, DedicatedServerArgs.ParsePort(null!, 1234));
	}
}
