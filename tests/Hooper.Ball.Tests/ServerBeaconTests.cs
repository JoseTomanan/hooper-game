using Hooper.Networking;

namespace Hooper.Ball.Tests;

/// <summary>
/// Tests the pure LAN-discovery beacon wire format (ADR-0007): the bytes a
/// dedicated server broadcasts and a browser client decodes. No engine —
/// PacketPeerUdp lives in DiscoveryBroadcaster/Listener, untested by design.
/// </summary>
public class ServerBeaconTests
{
	private static ServerBeacon Sample(string name = "Court A") =>
		new ServerBeacon(gamePort: 7777, curPlayers: 1, maxPlayers: 2, name: name);

	[Fact]
	public void Encode_then_decode_round_trips_all_fields()
	{
		ServerBeacon original = Sample();

		Assert.True(ServerBeacon.TryDecode(original.Encode(), out ServerBeacon decoded));
		Assert.Equal(original.GamePort, decoded.GamePort);
		Assert.Equal(original.CurPlayers, decoded.CurPlayers);
		Assert.Equal(original.MaxPlayers, decoded.MaxPlayers);
		Assert.Equal(original.Name, decoded.Name);
	}

	[Fact]
	public void Empty_name_round_trips()
	{
		ServerBeacon original = Sample(name: "");

		Assert.True(ServerBeacon.TryDecode(original.Encode(), out ServerBeacon decoded));
		Assert.Equal("", decoded.Name);
		Assert.Equal((ushort)7777, decoded.GamePort);
	}

	[Fact]
	public void GamePort_above_signed_short_round_trips()
	{
		// 50000 > 32767 — proves the port is read as an unsigned 16-bit value.
		var original = new ServerBeacon(gamePort: 50000, curPlayers: 0, maxPlayers: 2, name: "x");

		Assert.True(ServerBeacon.TryDecode(original.Encode(), out ServerBeacon decoded));
		Assert.Equal((ushort)50000, decoded.GamePort);
	}

	[Fact]
	public void Decode_rejects_wrong_magic()
	{
		byte[] bytes = Sample().Encode();
		bytes[0] = (byte)'X'; // corrupt the "HOOP" magic

		Assert.False(ServerBeacon.TryDecode(bytes, out _));
	}

	[Fact]
	public void Decode_rejects_wrong_version()
	{
		byte[] bytes = Sample().Encode();
		bytes[4] = 99; // version byte (after the 4-byte magic)

		Assert.False(ServerBeacon.TryDecode(bytes, out _));
	}

	[Fact]
	public void Decode_rejects_buffer_shorter_than_header()
	{
		Assert.False(ServerBeacon.TryDecode(new byte[] { (byte)'H', (byte)'O', (byte)'O' }, out _));
	}

	[Fact]
	public void Decode_rejects_null()
	{
		Assert.False(ServerBeacon.TryDecode(null!, out _));
	}

	[Fact]
	public void Decode_rejects_length_mismatch_truncated_name()
	{
		byte[] bytes = Sample("Court A").Encode();
		// Drop the last name byte without fixing nameLen → declared length no
		// longer matches the buffer; a partial/corrupt packet must be rejected.
		byte[] truncated = bytes[..^1];

		Assert.False(ServerBeacon.TryDecode(truncated, out _));
	}

	[Fact]
	public void Decode_rejects_trailing_garbage()
	{
		byte[] bytes = Sample().Encode();
		byte[] padded = new byte[bytes.Length + 3];
		bytes.CopyTo(padded, 0); // extra bytes beyond the declared name length

		Assert.False(ServerBeacon.TryDecode(padded, out _));
	}
}
