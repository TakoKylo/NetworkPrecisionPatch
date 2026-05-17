using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace MyPuckMod.Net
{
    /// <summary>
    /// Server-side half of the chunked-position sync system.  Tracks every
    /// SynchronizedObject's world position and, when one crosses a per-axis
    /// hysteresis threshold, announces a deferred chunk switch to all
    /// clients via the OWPMOD/Chunks CMM reliable message.
    ///
    /// Lifecycle:
    ///   - <see cref="Enable"/> is called by <see cref="NetworkBoundsPatch.EnableOpenWorldPrecision"/>
    ///     once chunks are the active mode.  Harmony prefixes are installed
    ///     on <c>Server_GatherSynchronizedObjectData</c> (the hysteresis
    ///     sweep + tick-id stash) and on <c>Server_ForceSynchronizeClientId</c>
    ///     (late-join bulk snapshot).  We also subscribe to the existing
    ///     SynchronizedObject spawn/despawn events so the tracked list stays
    ///     current.
    ///   - <see cref="Disable"/> unwinds everything.
    ///
    /// Hysteresis tuning:
    ///   - <see cref="FlipOutMeters"/> = 20 m.  Object must be more than 20 m
    ///     from its current chunk's center to trip a new pending switch.
    ///     Chunk size is 32 m so chunk-center is 16 m from any wall — there's
    ///     an 8 m deadband (12 m..20 m on either side) where neither chunk
    ///     triggers a flip.  No oscillation under realistic skate motion.
    ///   - <see cref="DeferTicks"/> = 50 ticks (~500 ms @ 100 Hz).  Gives the
    ///     reliable announce that much time to land before the first packet
    ///     encoded with the new chunk goes out.  Server keeps encoding with
    ///     the OLD chunk for those 50 ticks, so the object's distance from
    ///     the old chunk center can grow to 20 m (at trip) + 5 m (skate
    ///     speed × 500 ms) ≈ 25 m, still well inside the ±50 m wire range.
    /// </summary>
    public static class ChunkSyncServer
    {
        public const string CmmName = "OWPMOD/Chunks";

        // Hysteresis: trip a new pending switch when the object is further
        // than this from its current chunk's center on the relevant axis.
        // Greater than chunkSize/2 (16 m) so the player must overshoot the
        // nominal chunk wall by 4 m before we flip; combined with the fact
        // that the new chunk's center is 32 m away (so they land at 12 m
        // from the new center after flipping), this gives an 8 m deadband
        // and no flapping.
        private const float FlipOutMeters = 20f;

        // Defer the actual switch by this many server ticks.  Long enough
        // for the reliable announce to traverse normal network latency.
        // Server keeps encoding with OLD chunk during the defer window;
        // the wire range (±50 m) easily contains the further drift.
        private const int DeferTicks = 50;

        private const string HarmonyId = "openworldpractice.chunksync.server";

        private static Harmony _harmony;
        private static bool _enabled;
        private static readonly List<SynchronizedObject> _tracked = new List<SynchronizedObject>();

        // Reflection — read the manager's current tick id without inventing
        // our own counter.  Same source the existing RPC uses.
        private static FieldInfo _serverTickIdField;

        // ──────────────────────────────────────────────────────────────────
        // Lifecycle
        // ──────────────────────────────────────────────────────────────────

        public static void Enable()
        {
            if (_enabled) return;
            if (!NetworkRoles.IsServer)
            {
                Debug.Log("[OWP] ChunkSyncServer.Enable: not the server, skipping.");
                return;
            }

            _enabled = true;

            EventManager.AddEventListener("Event_Everyone_OnSynchronizedObjectSpawned", OnSpawnedEvent);
            EventManager.AddEventListener("Event_Everyone_OnSynchronizedObjectDespawned", OnDespawnedEvent);

            // Catch already-spawned synchronized objects.  Use the manager's
            // private list via reflection — much simpler than re-deriving it.
            var mgr = NetworkBehaviourSingleton<SynchronizedObjectManager>.Instance;
            if (mgr != null)
            {
                var listField = AccessTools.Field(typeof(SynchronizedObjectManager), "synchronizedObjects");
                if (listField?.GetValue(mgr) is System.Collections.IEnumerable list)
                {
                    foreach (var item in list)
                        if (item is SynchronizedObject so && !_tracked.Contains(so))
                        {
                            _tracked.Add(so);
                            InitializeSlotFor(so);
                        }
                }
            }

            _serverTickIdField = AccessTools.Field(typeof(SynchronizedObjectManager), "serverLastSentTickId");

            try
            {
                if (_harmony == null) _harmony = new Harmony(HarmonyId);

                var gather = AccessTools.Method(typeof(SynchronizedObjectManager), "Server_GatherSynchronizedObjectData");
                if (gather != null)
                    _harmony.Patch(gather, prefix: new HarmonyMethod(typeof(ChunkSyncServer), nameof(GatherPrefix)));
                else
                    Debug.LogWarning("[OWP] ChunkSyncServer: Server_GatherSynchronizedObjectData not found — hysteresis sweep disabled.");

                var forceSync = AccessTools.Method(typeof(SynchronizedObjectManager), "Server_ForceSynchronizeClientId");
                if (forceSync != null)
                    _harmony.Patch(forceSync, prefix: new HarmonyMethod(typeof(ChunkSyncServer), nameof(ForceSyncPrefix)));
                else
                    Debug.LogWarning("[OWP] ChunkSyncServer: Server_ForceSynchronizeClientId not found — late-join snapshot disabled.");

                Debug.Log("[OWP] ChunkSyncServer enabled (" + _tracked.Count + " already-spawned objects).");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[OWP] ChunkSyncServer patch install failed: " + ex.Message);
            }
        }

        public static void Disable()
        {
            if (!_enabled) return;
            _enabled = false;

            EventManager.RemoveEventListener("Event_Everyone_OnSynchronizedObjectSpawned", OnSpawnedEvent);
            EventManager.RemoveEventListener("Event_Everyone_OnSynchronizedObjectDespawned", OnDespawnedEvent);
            _tracked.Clear();

            if (_harmony != null)
            {
                try { _harmony.UnpatchSelf(); }
                catch (Exception ex) { Debug.LogWarning("[OWP] ChunkSyncServer UnpatchSelf failed: " + ex.Message); }
                _harmony = null;
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // Spawn / despawn — keep the tracked list in sync with the game.
        // ──────────────────────────────────────────────────────────────────

        private static void OnSpawnedEvent(Dictionary<string, object> msg)
        {
            if (!_enabled) return;
            if (!(msg["synchronizedObject"] is SynchronizedObject obj)) return;
            if (_tracked.Contains(obj)) return;
            _tracked.Add(obj);
            InitializeSlotFor(obj);
        }

        private static void OnDespawnedEvent(Dictionary<string, object> msg)
        {
            if (!_enabled) return;
            if (!(msg["synchronizedObject"] is SynchronizedObject obj)) return;
            _tracked.Remove(obj);
            ChunkRegistry.Remove((ushort)obj.NetworkObjectId);
        }

        private static void InitializeSlotFor(SynchronizedObject obj)
        {
            ushort id = (ushort)obj.NetworkObjectId;
            ChunkCoord chunk = WorldToChunk(obj.transform.position);
            // Apply locally as an instant set so our own encoder uses the right
            // chunk on the very first tick this object is sent.  Broadcast to
            // remote clients with the same instant-set marker so they likewise
            // initialize.  Host's own client side updates from the same
            // ApplyAnnounce here — CMM does NOT loop back to the sender.
            ChunkRegistry.ApplyAnnounce(id, chunk, ChunkRegistry.NoSwitchTickId);
            BroadcastInstant(id, chunk);
        }

        // ──────────────────────────────────────────────────────────────────
        // Per-tick hysteresis sweep — Harmony prefix on Server_GatherSynchronizedObjectData.
        //
        // Runs AFTER Server_ServerTick incremented serverLastSentTickId, but
        // BEFORE the gather encodes any object.  We:
        //   1. Stash the current tick id for encode helpers to read.
        //   2. For each tracked object, check hysteresis vs. its slot's
        //      resolved chunk at the current tick id.  A pending switch
        //      whose tick has arrived has already self-promoted inside
        //      ResolveAt — no extra book-keeping needed.
        //   3. If hysteresis trips, schedule a new pending switch and
        //      broadcast the announce.
        // ──────────────────────────────────────────────────────────────────

        // ReSharper disable once UnusedMember.Global
        public static void GatherPrefix()
        {
            if (!_enabled) return;

            ushort tick = GetCurrentTickId();
            ChunkRegistry.CurrentEncodeTickId = tick;

            for (int i = _tracked.Count - 1; i >= 0; i--)
            {
                var obj = _tracked[i];
                if (obj == null)
                {
                    _tracked.RemoveAt(i);
                    continue;
                }

                ushort id = (ushort)obj.NetworkObjectId;
                Vector3 worldPos = obj.transform.position;

                // Make sure a slot exists.  InitializeSlotFor handles new
                // spawns; this is a belt-and-braces for anything that snuck
                // into the tracked list without an explicit init.
                if (!ChunkRegistry.TryGet(id, out var slot))
                {
                    InitializeSlotFor(obj);
                    continue;
                }

                ChunkCoord active = slot.ResolveAt(tick);
                ChunkCoord target = HysteresisCheck(worldPos, active);

                if (target == active) continue;
                // Already pending toward this target?  Don't re-announce.
                if (slot.HasPending && slot.Pending == target && !TickGE(tick, slot.PendingTickId)) continue;

                ushort switchTick = (ushort)((tick + DeferTicks) % ushort.MaxValue);
                // Apply locally first so OUR encoder agrees with what we
                // just told everyone else; CMM doesn't loop back on the host.
                ChunkRegistry.ApplyAnnounce(id, target, switchTick);
                BroadcastSwitch(id, target, switchTick);
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // Late-join snapshot — Harmony prefix on Server_ForceSynchronizeClientId.
        //
        // Fired by SynchronizedObjectManagerController whenever a client
        // finishes its scene sync.  We pre-emptively push our full chunk
        // table to the joining client; the reliable channel guarantees it
        // lands eventually, and the client-side reject filter covers the
        // ~RTT window where unreliable position packets may decode wrong.
        // ──────────────────────────────────────────────────────────────────

        // ReSharper disable once UnusedMember.Global
        public static void ForceSyncPrefix(ulong clientId)
        {
            if (!_enabled) return;
            if (ChunkRegistry.Count == 0) return;

            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;
            var cmm = nm.CustomMessagingManager;
            if (cmm == null) return;
            // Don't send to ourselves on a host — local ChunkSyncServer
            // already kept the host's registry in lockstep.
            if (nm.IsHost && clientId == nm.LocalClientId) return;

            try
            {
                // Header (1 byte type + 2 bytes count) + N * (2 + 1 + 1 + 2) = 6 per entry.
                int est = 3 + ChunkRegistry.Count * 6 + 64;
                var writer = new FastBufferWriter(est, Allocator.Temp, est * 4);
                try
                {
                    writer.WriteValueSafe((byte)1); // bulk marker
                    writer.WriteValueSafe((ushort)ChunkRegistry.Count);
                    foreach (var kvp in ChunkRegistry.Snapshot())
                    {
                        // Use Current as the late-joiner's starting chunk —
                        // any Pending will be re-announced naturally on the
                        // next hysteresis check.  Always instant-apply.
                        writer.WriteValueSafe(kvp.Key);
                        writer.WriteValueSafe(kvp.Value.Current.X);
                        writer.WriteValueSafe(kvp.Value.Current.Z);
                        writer.WriteValueSafe(ChunkRegistry.NoSwitchTickId);
                    }
                    cmm.SendNamedMessage(CmmName, clientId, writer, NetworkDelivery.Reliable);
                }
                finally { writer.Dispose(); }
                Debug.Log("[OWP] ChunkSyncServer: sent bulk snapshot (" + ChunkRegistry.Count
                          + " slots) to client " + clientId + ".");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[OWP] ChunkSyncServer.ForceSync failed: " + ex.Message);
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // CMM senders
        // ──────────────────────────────────────────────────────────────────

        private static void BroadcastInstant(ushort id, ChunkCoord chunk)
        {
            // type 0 = single update, switchTick = NoSwitchTickId means
            // "apply immediately" on the receiver.
            BroadcastSingle(id, chunk, ChunkRegistry.NoSwitchTickId);
        }

        private static void BroadcastSwitch(ushort id, ChunkCoord chunk, ushort switchTickId)
        {
            BroadcastSingle(id, chunk, switchTickId);
        }

        private static void BroadcastSingle(ushort id, ChunkCoord chunk, ushort switchTickId)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;
            var cmm = nm.CustomMessagingManager;
            if (cmm == null) return;

            // No remote clients = nothing to send.  Host's own client side
            // already updated its slot via ApplyAnnounce.
            int remote = nm.IsHost ? nm.ConnectedClientsIds.Count - 1 : nm.ConnectedClientsIds.Count;
            if (remote <= 0) return;

            try
            {
                // 1 (type) + 2 (id) + 1 (X) + 1 (Z) + 2 (switchTick) = 7 bytes.
                var writer = new FastBufferWriter(8, Allocator.Temp, 32);
                try
                {
                    writer.WriteValueSafe((byte)0); // single
                    writer.WriteValueSafe(id);
                    writer.WriteValueSafe(chunk.X);
                    writer.WriteValueSafe(chunk.Z);
                    writer.WriteValueSafe(switchTickId);
                    cmm.SendNamedMessageToAll(CmmName, writer, NetworkDelivery.Reliable);
                }
                finally { writer.Dispose(); }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[OWP] ChunkSyncServer.BroadcastSingle failed: " + ex.Message);
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // Math helpers
        // ──────────────────────────────────────────────────────────────────

        private static ChunkCoord WorldToChunk(Vector3 pos)
        {
            int cx = Mathf.RoundToInt(pos.x / ChunkRegistry.ChunkSizeMeters);
            int cz = Mathf.RoundToInt(pos.z / ChunkRegistry.ChunkSizeMeters);
            cx = Mathf.Clamp(cx, sbyte.MinValue, sbyte.MaxValue);
            cz = Mathf.Clamp(cz, sbyte.MinValue, sbyte.MaxValue);
            return new ChunkCoord((sbyte)cx, (sbyte)cz);
        }

        private static ChunkCoord HysteresisCheck(Vector3 worldPos, ChunkCoord current)
        {
            float cx = current.X * ChunkRegistry.ChunkSizeMeters;
            float cz = current.Z * ChunkRegistry.ChunkSizeMeters;
            float dx = worldPos.x - cx;
            float dz = worldPos.z - cz;

            int nx = current.X;
            int nz = current.Z;
            // Per-axis flip — diagonal crossings can move both at once,
            // emitting a single combined announce.
            if      (dx >  FlipOutMeters) nx = Mathf.Clamp(current.X + 1, sbyte.MinValue, sbyte.MaxValue);
            else if (dx < -FlipOutMeters) nx = Mathf.Clamp(current.X - 1, sbyte.MinValue, sbyte.MaxValue);
            if      (dz >  FlipOutMeters) nz = Mathf.Clamp(current.Z + 1, sbyte.MinValue, sbyte.MaxValue);
            else if (dz < -FlipOutMeters) nz = Mathf.Clamp(current.Z - 1, sbyte.MinValue, sbyte.MaxValue);
            return new ChunkCoord((sbyte)nx, (sbyte)nz);
        }

        private static ushort GetCurrentTickId()
        {
            var mgr = NetworkBehaviourSingleton<SynchronizedObjectManager>.Instance;
            if (mgr == null || _serverTickIdField == null) return 0;
            try { return (ushort)_serverTickIdField.GetValue(mgr); }
            catch { return 0; }
        }

        private static bool TickGE(ushort a, ushort b) => (ushort)(a - b) < 32768;
    }
}
