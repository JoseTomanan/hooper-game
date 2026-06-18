using System;
using System.Text;

namespace Hooper.Networking;

/// <summary>
/// The LAN-discovery beacon: the payload a dedicated server broadcasts ~1 Hz and
/// a browser client decodes (ADR-0007). Pure — no Godot Node, no engine
/// singletons — so the wire format is unit-testable via the GodotSharp seam; the
/// UDP socket lives in DiscoveryBroadcaster / DiscoveryListener.
///
/// ── Wire format (little headroom, fixed header + bounded name) ──────────────
///   offset  size  field
///   0       4     magic  "HOOP" (ASCII)
///   4       1     protocol version
///   5       2     game port, BIG-ENDIAN unsigned 16-bit
///   7       1     current players
///   8       1     max players
///   9       1     name length (bytes, 0..255)
///   10      N     name, UTF-8
///
/// The source IP is NOT in the packet — the receiver reads it from the UDP
/// sender address (a server can't be trusted to self-report its address, and the
/// transport already knows it). Game port IS in the packet, because the discovery
/// port (7778) differs from the game port the client must dial to actually join.
///
/// Port is written big-endian explicitly rather than via BitConverter so the
/// format is defined by this spec, not by the encoding CPU's endianness.
/// </summary>
public readonly struct ServerBeacon
{
	/// <summary>Bump when the wire format changes; decoders reject other versions.</summary>
	public const byte ProtocolVersion = 1;

	/// <summary>Fixed bytes before the variable-length name (magic..nameLen).</summary>
	private const int HeaderSize = 10;

	public ushort GamePort { get; }
	public byte CurPlayers { get; }
	public byte MaxPlayers { get; }
	public string Name { get; }

	public ServerBeacon(ushort gamePort, byte curPlayers, byte maxPlayers, string name)
	{
		GamePort   = gamePort;
		CurPlayers = curPlayers;
		MaxPlayers = maxPlayers;
		Name       = name ?? "";
	}

	/// <summary>Serializes this beacon to its wire bytes.</summary>
	public byte[] Encode()
	{
		byte[] nameBytes = Encoding.UTF8.GetBytes(Name);
		// nameLen is a single byte; clamp so an over-long name can never overflow
		// it or desync the declared length from the buffer.
		if (nameBytes.Length > 255)
			Array.Resize(ref nameBytes, 255);

		byte[] buf = new byte[HeaderSize + nameBytes.Length];
		buf[0] = (byte)'H';
		buf[1] = (byte)'O';
		buf[2] = (byte)'O';
		buf[3] = (byte)'P';
		buf[4] = ProtocolVersion;
		buf[5] = (byte)(GamePort >> 8);   // big-endian high byte
		buf[6] = (byte)(GamePort & 0xFF); // big-endian low byte
		buf[7] = CurPlayers;
		buf[8] = MaxPlayers;
		buf[9] = (byte)nameBytes.Length;
		nameBytes.CopyTo(buf, HeaderSize);
		return buf;
	}

	/// <summary>
	/// Parses wire bytes back into a beacon. Returns false (no throw) for any
	/// malformed input — wrong magic, unknown version, too short, or a declared
	/// name length that does not exactly match the buffer (truncated or padded).
	/// A discovery listener receives arbitrary UDP traffic, so strict rejection
	/// of anything that is not exactly our format is the point.
	/// </summary>
	public static bool TryDecode(byte[] data, out ServerBeacon beacon)
	{
		beacon = default;

		if (data == null || data.Length < HeaderSize)
			return false;

		if (data[0] != (byte)'H' || data[1] != (byte)'O' ||
		    data[2] != (byte)'O' || data[3] != (byte)'P')
			return false;

		if (data[4] != ProtocolVersion)
			return false;

		int nameLen = data[9];
		// Exact-length check rejects both a truncated packet and trailing garbage.
		if (data.Length != HeaderSize + nameLen)
			return false;

		ushort gamePort = (ushort)((data[5] << 8) | data[6]);
		byte curPlayers = data[7];
		byte maxPlayers = data[8];
		string name = Encoding.UTF8.GetString(data, HeaderSize, nameLen);

		beacon = new ServerBeacon(gamePort, curPlayers, maxPlayers, name);
		return true;
	}
}
