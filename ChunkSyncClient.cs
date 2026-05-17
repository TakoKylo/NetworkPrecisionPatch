using System;
using System.Collections.Generic;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;

namespace MyPuckMod.Net
{
    /// <summary>
    /// Client-side half of the chunked-position sync system.
    ///
    /// Responsibilities:
    ///   1. Receive OWPMOD/Chunks reliable messages and apply them to the
    ///      <see cref="ChunkRegistry"/>.  Single updates carry a future
    ///      switch tickId so the offset doesn't take effect mid-handoff;
    ///      bulk snapshots (sent on late join) carry the instant-apply
    ///      sentinel and overwrite Current directly.
    ///   2. Stash the incoming RPC's tickId on <see cref="ChunkRegistry.CurrentDecodeTickId"/>
    ///      before the original RPC body runs the decode loop.  Decode
    ///      helpers read from there to resolve each slot's chunk for the
    ///      packet's tickId.
    ///   3. Reject-filter incoming positions whose decoded delta from the
    ///      last-known position exceeds the chunk size.  This is the safety
    ///      net for the rare cross-channel race where an unreliable position
    ///      packet beats its reliable chunk announce to the client.  At most
    ///      <see cref="MaxConsecutiveDrops"/> packets are dropped before we
    ///      accept anyway, so legitimate teleports recover within ~50 ms.
    ///
    /// CMM handler registration is owned by <see cref="OpenWorldNetworkSync"/>
    /// so all CMM handler registration lives in one place.  We just publish
    /// the handler delegate; OWNS calls it whenever a OWPMOD/Chunks packet
    /// arrives.
    /// </summary>
    public static class ChunkSyncClient
    {
        // Reject if the absolute delta on x OR z exceeds this many metres.
        // chunkSize/2 = 16 m, so 16 m comfortably catches a 32 m chunk-mismatch
        // glitch while letting normal motion through (skate speed × tick =
        // ~0.15 m).  Also catches the "uninitialized slot decodes wrong by
        // a full chunk" case during the spawn-vs-announce race.
        private const float RejectThresholdMeters = 16f;

        // Stop dropping after this many in a row so a real teleport (or a
        // dropped announce that never recovers) doesn't freeze an object
        // forever.  50 ms @ 100 Hz is well below human perception.
        private const int MaxConsecutiveDrops = 5;

        private const string HarmonyId = "openworldpractice.chunksync.client";

        private static Harmony _harmony;
        private static bool _enabled;

        // Per-object reject-filter state.  Keyed on the ushort NetworkObjectId
        // that the encode/decode methods already use.
        private struct FilterState
        {
            public Vector3 LastDecoded;
            public int     ConsecutiveDrops;
            public bool    Initialized;
        }
        private static readonly Dictionary<ushort, FilterState> _filter
            = new Dictionary<ushort, FilterState>();

        // ──────────────────────────────────────────────────────────────────
        // Lifecycle
        // ──────────────────────────────────────────────────────────────────

        public static void Enable()
        {
            if (_enabled) return;
            _enabled = true;
            _filter.Clear();

            try
            {
                if (_harmony == null) _harmony = new Harmony(HarmonyId);

                // Prefix on the RPC method captures tickId for decode helpers.
                // The RPC method runs in both send and execute stages — the
                // value we stash is harmless in the send branch (nobody reads
                // it before execute overwrites) and correct in the execute
                // branch (where decoding actually happens).
                var rpc = AccessTools.Method(typeof(SynchronizedObjectManager), "Server_SynchronizeObjectsRpc");
                if (rpc != null)
                    _harmony.Patch(rpc, prefix: new HarmonyMethod(typeof(ChunkSyncClient), nameof(RpcPrefix)));
                else
                    Debug.LogWarning("[OWP] ChunkSyncClient: Server_SynchronizeObjectsRpc not found — tickId stash disabled.");

                // Prefix on OnClientTick / OnClientSmoothTick: filter the
                // incoming position.  We modify position by ref so the
                // original method writes whatever we leave there — replacing
                // bad packets with the last good position effectively pauses
                // the object until the next valid packet.
                var tick   = AccessTools.Method(typeof(SynchronizedObject), "OnClientTick");
                var smooth = AccessTools.Method(typeof(SynchronizedObject), "OnClientSmoothTick");
                if (tick != null)
                    _harmony.Patch(tick,   prefix: new HarmonyMethod(typeof(ChunkSyncClient), nameof(OnClientTickPrefix)));
                if (smooth != null)
                    _harmony.Patch(smooth, prefix: new HarmonyMethod(typeof(ChunkSyncClient), nameof(OnClientSmoothTickPrefix)));

                // Track despawns to drop filter state for vanished ids.
                EventManager.AddEventListener("Event_Everyone_OnSynchronizedObjectDespawned", OnDespawnedEvent);

                Debug.Log("[OWP] ChunkSyncClient enabled (filter threshold " + RejectThresholdMeters
                          + " m, max " + MaxConsecutiveDrops + " consecutive drops).");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[OWP] ChunkSyncClient patch install failed: " + ex.Message);
            }
        }

        public static void Disable()
        {
            if (!_enabled) return;
            _enabled = false;
            _filter.Clear();

            EventManager.RemoveEventListener("Event_Everyone_OnSynchronizedObjectDespawned", OnDespawnedEvent);

            if (_harmony != null)
            {
                try { _harmony.UnpatchSelf(); }
                catch (Exception ex) { Debug.LogWarning("[OWP] ChunkSyncClient UnpatchSelf failed: " + ex.Message); }
                _harmony = null;
            }
        }

        private static void OnDespawnedEvent(Dictionary<string, object> msg)
        {
            if (!(msg["synchronizedObject"] is SynchronizedObject obj)) return;
            _filter.Remove((ushort)obj.NetworkObjectId);
        }

        // ──────────────────────────────────────────────────────────────────
        // CMM message handler — registered by OpenWorldNetworkSync.
        // Wire format:
        //   byte type    (0 = single, 1 = bulk)
        //   single:  ushort id, sbyte cx, sbyte cz, ushort switchTickId
        //   bulk:    ushort count, then count × { ushort id, sbyte cx, sbyte cz, ushort switchTickId }
        // switchTickId == ChunkRegistry.NoSwitchTickId (0xFFFF) means
        // "apply immediately", used by the late-join bulk and the spawn
        // initial-state broadcast.
        // ──────────────────────────────────────────────────────────────────

        public static void OnChunkMessage(ulong senderId, FastBufferReader reader)
        {
            // Host doesn't receive its own CMM broadcasts — server-side code
            // (ChunkSyncServer) already updates the registry for us.  This
            // guard is mostly belt-and-braces.
            if (NetworkRoles.IsServer) return;

            try
            {
                reader.ReadValueSafe(out byte type);
                if (type == 0)
                {
                    ReadSingle(reader);
                }
                else if (type == 1)
                {
                    reader.ReadValueSafe(out ushort count);
                    for (int i = 0; i < count; i++)
                        ReadSingle(reader);
                    Debug.Log("[OWP] ChunkSyncClient: applied bulk snapshot (" + count + " slots).");
                }
                else
                {
                    Debug.LogWarning("[OWP] ChunkSyncClient: unknown OWPMOD/Chunks type " + type + ".");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[OWP] ChunkSyncClient.OnChunkMessage decode failed: " + ex.Message);
            }
        }

        private static void ReadSingle(FastBufferReader reader)
        {
            reader.ReadValueSafe(out ushort id);
            reader.ReadValueSafe(out sbyte  cx);
            reader.ReadValueSafe(out sbyte  cz);
            reader.ReadValueSafe(out ushort switchTick);
            var chunk = new ChunkCoord(cx, cz);
            ChunkRegistry.ApplyAnnounce(id, chunk, switchTick);
            // Pre-seed the filter's baseline to the chunk centre so the very
            // next position packet — which will decode to somewhere inside
            // this chunk's wire range — is accepted instead of being treated
            // as a teleport from (0,0,0).  Only seeds if filter is still
            // uninitialised (don't clobber an already-running baseline).
            SeedFilterBaseline(id, chunk);
        }

        private static void SeedFilterBaseline(ushort id, ChunkCoord chunk)
        {
            if (_filter.TryGetValue(id, out var s) && s.Initialized) return;
            s.LastDecoded = new Vector3(
                chunk.X * ChunkRegistry.ChunkSizeMeters,
                0f,
                chunk.Z * ChunkRegistry.ChunkSizeMeters);
            s.Initialized = true;
            s.ConsecutiveDrops = 0;
            _filter[id] = s;
        }

        // ──────────────────────────────────────────────────────────────────
        // Harmony patches
        // ──────────────────────────────────────────────────────────────────

        // ReSharper disable once UnusedMember.Global
        public static void RpcPrefix(ushort tickId)
        {
            ChunkRegistry.CurrentDecodeTickId = tickId;
        }

        // ReSharper disable once UnusedMember.Global
        public static void OnClientTickPrefix(SynchronizedObject __instance, ref Vector3 position)
        {
            if (!_enabled || __instance == null) return;
            // Server / host: vanilla snap path doesn't need filtering.
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer) return;

            FilterAndPossiblyReplace(__instance, ref position);
        }

        // ReSharper disable once UnusedMember.Global
        public static void OnClientSmoothTickPrefix(SynchronizedObject __instance, ref Vector3 position)
        {
            if (!_enabled || __instance == null) return;
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer) return;

            FilterAndPossiblyReplace(__instance, ref position);
        }

        // Replaces `position` with a safe substitute when:
        //   1. Chunks are active but no slot has arrived yet for this id —
        //      we can't trust the decode, so pin to the current transform
        //      position (OnClientTick will write transform.position to
        //      transform.position; a one-tick no-op).
        //   2. The decoded delta from the last good value exceeds chunkSize/2
        //      on either X or Z, capped at MaxConsecutiveDrops in a row.
        //      Drops are the safety net for the rare cross-channel race
        //      where an unreliable position packet beats a reliable chunk
        //      announce.  After the cap, we accept anyway so legitimate
        //      teleports (or a missing announce) eventually land.
        private static void FilterAndPossiblyReplace(SynchronizedObject obj, ref Vector3 position)
        {
            ushort id = (ushort)obj.NetworkObjectId;

            // Case 1: chunks active but no slot — refuse to update.
            if (ChunkRegistry.ChunksActive && !ChunkRegistry.TryGet(id, out _))
            {
                position = obj.transform.position;
                return;
            }

            _filter.TryGetValue(id, out var s);

            if (!s.Initialized)
            {
                // First-ever decoded position for this id.  Accept it — we
                // have a slot (else case 1 caught it) and the decoded value
                // is presumed valid.  Records the baseline.
                s.LastDecoded = position;
                s.Initialized = true;
                s.ConsecutiveDrops = 0;
                _filter[id] = s;
                return;
            }

            float dx = Mathf.Abs(position.x - s.LastDecoded.x);
            float dz = Mathf.Abs(position.z - s.LastDecoded.z);

            if ((dx > RejectThresholdMeters || dz > RejectThresholdMeters)
                && s.ConsecutiveDrops < MaxConsecutiveDrops)
            {
                // Suspect packet — pin position to last good so the object
                // freezes for one tick rather than teleporting by ~chunkSize.
                position = s.LastDecoded;
                s.ConsecutiveDrops++;
                _filter[id] = s;
                return;
            }

            // Accept.  Reset the drop counter and roll the baseline forward.
            s.LastDecoded = position;
            s.ConsecutiveDrops = 0;
            _filter[id] = s;
        }
    }
}
