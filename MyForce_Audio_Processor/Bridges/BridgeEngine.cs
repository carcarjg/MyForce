// %%%%%%    @%%%%%@
// Copyright (C) 2025-2026 NyxTel Wireless / Nyx Gallini
//
// PROJECT_FRAMEWORK.md Phase 2: the Bridge Engine (§3.5). It consumes the normalized Call
// Detect (RX-active) signal the AP derives from VOX/COR (§3.6.8), arbitrates which member
// "holds" each bridge, keys the other members through the AP keying primitives, and routes
// the holder's RX to every other member's TX (a mix-minus conference, §3.5). Multiple bridges
// run concurrently and are NOT subject to the manual one-at-a-time limit.
//
// Arbitration rules (§3.5):
//   - First-come-wins: the member that started receiving first holds the bridge.
//   - Priority is a tiebreaker only, applied when two members come up together.
//   - A member already holding the bridge is never interrupted.
//   - Hang-time: the patch stays keyed for a short configurable hang after Call Detect drops
//     so brief squelch gaps mid-transmission do not drop and re-key the patch (§3.6.8).
//
// This is pure control-thread logic (deterministic given the detect/tx maps and a clock), so
// it is fully testable without audio hardware. Audio actually flows once radios are bound to
// real soundcards (Phase 3); until then the arbitration, routing snapshot, and state are live.

using System.Collections.ObjectModel;

/// <summary>A bridge member: the radio plus its priority tiebreaker and TX gain (§5.8.7).</summary>
internal sealed record BridgeMember(RadioId RadioId, int Priority, double TxGainDb)
{
	/// <summary>Per-crosspoint linear gain applied to the holder's RX feeding this member's TX.</summary>
	public float LinearTxGain => (float)Math.Pow(10.0, TxGainDb / 20.0);
}

/// <summary>A configured bridge (cross-patch / conference) of two or more radios (§3.5).</summary>
internal sealed record BridgeDefinition(BridgeId Id, string Alias, ReadOnlyCollection<BridgeMember> Members, int HangMs, bool Enabled);

/// <summary>One active routing crosspoint a bridge contributes: holder RX -> member TX at a gain.</summary>
internal sealed record BridgeRoutingEdge(RadioId SourceRx, RadioId SinkTx, float Gain);

/// <summary>Result of one bridge-engine tick across all bridges.</summary>
internal sealed record BridgeEvaluation(
	IReadOnlyCollection<RadioId> DesiredKeyedMembers,
	IReadOnlyList<BridgeRoutingEdge> RoutingEdges,
	bool StateChanged);

/// <summary>Live per-member activity for the bridge state topic (§5.8.7).</summary>
internal sealed record BridgeMemberState(string RadioId, bool RxActive, bool TxActive);

/// <summary>Live bridge state snapshot for MQTT (§5.8.7).</summary>
internal sealed record BridgeStateSnapshot(string Id, bool Active, string? Holder, IReadOnlyList<BridgeMemberState> Members);

internal sealed class BridgeEngine
{
	private readonly Dictionary<string, BridgeRuntime> _bridges;
	private readonly Action<string, string> _log;

	public BridgeEngine(IReadOnlyList<BridgeDefinition> definitions, Action<string, string> log)
	{
		ArgumentNullException.ThrowIfNull(definitions);
		ArgumentNullException.ThrowIfNull(log);
		_log = log;
		_bridges = definitions.ToDictionary(
			static definition => definition.Id.Value,
			static definition => new BridgeRuntime(definition),
			StringComparer.OrdinalIgnoreCase);
	}

	public IReadOnlyList<BridgeDefinition> Definitions => _bridges.Values.Select(static bridge => bridge.Definition).ToArray();

	/// <summary>
	/// Evaluate every bridge against the current Call Detect and manual-TX maps. Returns the union of
	/// members that should be bridge-keyed, the active routing edges, and whether any bridge changed.
	/// </summary>
	public BridgeEvaluation Evaluate(IReadOnlyDictionary<string, bool> callDetect, IReadOnlyDictionary<string, bool> manualTxActive, long nowMs)
	{
		ArgumentNullException.ThrowIfNull(callDetect);
		ArgumentNullException.ThrowIfNull(manualTxActive);

		var desiredKeyed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var edges = new List<BridgeRoutingEdge>();
		var changed = false;

		foreach (var bridge in _bridges.Values)
		{
			if (bridge.Evaluate(callDetect, manualTxActive, nowMs, desiredKeyed, edges))
			{
				changed = true;
			}
		}

		return new BridgeEvaluation(
			desiredKeyed.Select(static value => new RadioId(value)).ToArray(),
			edges,
			changed);
	}

	/// <summary>True when some bridge is actively repeating onto this radio (drives the §3.5 lockout).</summary>
	public bool IsRadioBridgeKeyed(RadioId radioId)
	{
		ArgumentNullException.ThrowIfNull(radioId);
		return _bridges.Values.Any(bridge => bridge.IsKeying(radioId));
	}

	public IReadOnlyList<BridgeStateSnapshot> GetState(IReadOnlyDictionary<string, bool> callDetect)
	{
		ArgumentNullException.ThrowIfNull(callDetect);
		return _bridges.Values.Select(bridge => bridge.ToStateSnapshot(callDetect)).ToArray();
	}

	public bool Contains(string bridgeId) => _bridges.ContainsKey(bridgeId);

	public bool TrySetEnabled(string bridgeId, bool enabled)
	{
		if (!_bridges.TryGetValue(bridgeId, out var bridge))
		{
			return false;
		}

		bridge.SetEnabled(enabled);
		_log("bridge", $"Bridge '{bridgeId}' {(enabled ? "enabled" : "disabled")}.");
		return true;
	}

	/// <summary>Create or replace a bridge definition at runtime (cmd/config, §5.8.7).</summary>
	public void Upsert(BridgeDefinition definition)
	{
		ArgumentNullException.ThrowIfNull(definition);
		_bridges[definition.Id.Value] = new BridgeRuntime(definition);
		_log("bridge", $"Bridge '{definition.Id.Value}' definition applied ({definition.Members.Count} member(s), hang {definition.HangMs} ms).");
	}

	public bool Remove(string bridgeId) => _bridges.Remove(bridgeId);

	/// <summary>Per-bridge runtime: holder selection, hang, keyed members, and routing edges.</summary>
	private sealed class BridgeRuntime
	{
		private readonly Dictionary<string, long> _rxActiveSinceMs = new(StringComparer.OrdinalIgnoreCase);
		private readonly HashSet<string> _keyedMembers = new(StringComparer.OrdinalIgnoreCase);
		private string? _holder;
		private long _holderLastActiveMs;

		public BridgeRuntime(BridgeDefinition definition)
		{
			Definition = definition;
		}

		public BridgeDefinition Definition { get; private set; }

		public bool IsKeying(RadioId radioId) => _keyedMembers.Contains(radioId.Value);

		public void SetEnabled(bool enabled) => Definition = Definition with { Enabled = enabled };

		public bool Evaluate(
			IReadOnlyDictionary<string, bool> callDetect,
			IReadOnlyDictionary<string, bool> manualTxActive,
			long nowMs,
			ISet<string> desiredKeyedAccumulator,
			List<BridgeRoutingEdge> edgeAccumulator)
		{
			var previousHolder = _holder;
			var previousKeyedCount = _keyedMembers.Count;
			_keyedMembers.Clear();

			if (!Definition.Enabled || Definition.Members.Count < 2)
			{
				_holder = null;
				_rxActiveSinceMs.Clear();
				return previousHolder is not null || previousKeyedCount != 0;
			}

			// Track when each member started receiving (first-come ordering). A member that is
			// manually transmitting is not eligible to hold the bridge (it is talking, not receiving).
			foreach (var member in Definition.Members)
			{
				var key = member.RadioId.Value;
				var isReceiving = callDetect.GetValueOrDefault(key) && !manualTxActive.GetValueOrDefault(key);
				if (isReceiving)
				{
					if (!_rxActiveSinceMs.ContainsKey(key))
					{
						_rxActiveSinceMs[key] = nowMs;
					}
				}
				else
				{
					_rxActiveSinceMs.Remove(key);
				}
			}

			// Hold management: keep the current holder while it receives, plus a hang after it drops.
			if (_holder is not null)
			{
				if (callDetect.GetValueOrDefault(_holder) && !manualTxActive.GetValueOrDefault(_holder))
				{
					_holderLastActiveMs = nowMs;
				}
				else if (nowMs - _holderLastActiveMs >= Definition.HangMs)
				{
					_holder = null;
				}
			}

			// First-come-wins, priority as the tiebreaker only. Never interrupts an existing holder.
			if (_holder is null)
			{
				var candidate = Definition.Members
					.Where(member => _rxActiveSinceMs.ContainsKey(member.RadioId.Value))
					.OrderBy(member => _rxActiveSinceMs[member.RadioId.Value])
					.ThenBy(member => member.Priority)
					.FirstOrDefault();

				if (candidate is not null)
				{
					_holder = candidate.RadioId.Value;
					_holderLastActiveMs = nowMs;
				}
			}

			// While held, repeat the holder's RX to every OTHER member's TX (mix-minus: a member
			// never carries its own RX, §3.5). Half-duplex serialization means only the holder feeds.
			if (_holder is not null)
			{
				var holderRadio = new RadioId(_holder);
				foreach (var member in Definition.Members)
				{
					if (string.Equals(member.RadioId.Value, _holder, StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}

					_keyedMembers.Add(member.RadioId.Value);
					desiredKeyedAccumulator.Add(member.RadioId.Value);
					edgeAccumulator.Add(new BridgeRoutingEdge(holderRadio, member.RadioId, member.LinearTxGain));
				}
			}

			return previousHolder != _holder || previousKeyedCount != _keyedMembers.Count;
		}

		public BridgeStateSnapshot ToStateSnapshot(IReadOnlyDictionary<string, bool> callDetect)
		{
			var members = Definition.Members
				.Select(member => new BridgeMemberState(
					member.RadioId.Value,
					RxActive: callDetect.GetValueOrDefault(member.RadioId.Value),
					TxActive: _keyedMembers.Contains(member.RadioId.Value)))
				.ToArray();

			return new BridgeStateSnapshot(Definition.Id.Value, _holder is not null, _holder, members);
		}
	}
}
