using System.Collections.Generic;

namespace Hooper.Networking;

/// <summary>One discovered server, as the browser displays it (ADR-0007).</summary>
public sealed class ServerListEntry
{
	/// <summary>Source IP, read from the UDP sender address (not from the packet).</summary>
	public string Ip { get; init; } = "";

	/// <summary>Port to dial when joining — from the beacon, distinct from the discovery port.</summary>
	public ushort GamePort { get; init; }

	public string Name { get; set; } = "";
	public byte CurPlayers { get; set; }
	public byte MaxPlayers { get; set; }

	/// <summary>Clock value (seconds) of the most recent beacon; drives expiry.</summary>
	public double LastSeen { get; set; }
}

/// <summary>
/// Pure bookkeeping for discovered servers (ADR-0007): dedupe by (ip, gamePort),
/// refresh on each beacon, and expire entries that stop broadcasting. No engine,
/// no real clock — the caller (DiscoveryListener) injects <c>now</c>, which makes
/// expiry deterministic and unit-testable without sleeping.
///
/// Keyed by (ip, gamePort), not ip alone, so one machine can legitimately host
/// two servers on different ports and both show up.
/// </summary>
public sealed class ServerList
{
	private readonly Dictionary<string, ServerListEntry> _byKey = new();

	/// <summary>A fresh snapshot of the current servers, safe for the caller to iterate.</summary>
	public IReadOnlyList<ServerListEntry> Servers => new List<ServerListEntry>(_byKey.Values);

	private static string Key(string ip, ushort gamePort) => ip + ":" + gamePort;

	/// <summary>
	/// Records a beacon from <paramref name="ip"/>: inserts a new entry or updates
	/// the existing one for that (ip, gamePort), and stamps <paramref name="now"/>
	/// as its LastSeen. Mutable fields (name, player counts) take the newest
	/// beacon's values so a filling/emptying server reflects live.
	/// </summary>
	public void Observe(string ip, ServerBeacon beacon, double now)
	{
		string key = Key(ip, beacon.GamePort);

		// ContainsKey + indexer (not out-var) keeps this nullable-clean without a
		// "?" annotation, which would warn in the game build (Nullable off there,
		// on in the test project) — the same cross-project divergence the rest of
		// the shared pure files avoid. The double lookup is irrelevant at LAN scale.
		if (!_byKey.ContainsKey(key))
			_byKey[key] = new ServerListEntry { Ip = ip, GamePort = beacon.GamePort };

		ServerListEntry entry = _byKey[key];
		entry.Name       = beacon.Name;
		entry.CurPlayers = beacon.CurPlayers;
		entry.MaxPlayers = beacon.MaxPlayers;
		entry.LastSeen   = now;
	}

	/// <summary>
	/// Removes entries not seen within <paramref name="timeoutSeconds"/> of
	/// <paramref name="now"/> (a server that went silent — closed, crashed, or off
	/// the network). Returns how many were removed.
	/// </summary>
	public int PruneExpired(double now, double timeoutSeconds)
	{
		var stale = new List<string>();

		foreach (KeyValuePair<string, ServerListEntry> kv in _byKey)
		{
			if (now - kv.Value.LastSeen > timeoutSeconds)
				stale.Add(kv.Key);
		}

		foreach (string key in stale)
			_byKey.Remove(key);

		return stale.Count;
	}
}
