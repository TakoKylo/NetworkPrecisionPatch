using System.Collections.Generic;
using UnityEngine;

namespace MyPuckMod.Net
{
    /// <summary>
    /// Per-object chunk offset table.  Every networked object is treated as
    /// living inside a 32 × 32 m chunk on X/Z (Y is not chunked).  Position
    /// quantisation encodes the world position MINUS the chunk origin, so the
    /// wire value is always within ±50 m of zero — comfortably inside the
    /// ±50 m range a signed 16-bit short × 655f precision affords.  Decoding
    /// adds the chunk origin back, so the round-trip is exact whenever the
    /// chunk offset is an integer multiple of (1 / precision).  We size
    /// chunks to a clean multiple of 655 (32 × 655 = 20960, integer) so the
    /// round-trip is bit-exact regardless of where in the chunk the object
    /// sits.
    ///
    /// Handoffs are deferred and synchronized by tickId.  When the server
    /// decides to switch an object's chunk, it announces a future tickId
    /// when the new chunk goes live.  Both server (encoding) and client
    /// (decoding) compute their per-packet offset from the slot's resolved
    /// chunk at the packet's tickId — pre-switch tickIds use Current,
    /// post-switch tickIds use Pending.  The reliable announce has ~500 ms
    /// (50 ticks @ 100 Hz) to reach the client before the first post-switch
    /// position packet arrives.  If it doesn't, the client-side reject filter
    /// in <see cref="ChunkSyncClient"/> drops the mismatched packets until
    /// the next reliable announce catches up.
    ///
    /// This class is the seam between three subsystems:
    ///   1. Transpiled <see cref="SynchronizedObjectManager"/> encode/decode
    ///      methods call EncodeX/Y/Z and DecodeX/Y/Z below.
    ///   2. <see cref="ChunkSyncServer"/> writes slots when an object crosses
    ///      a chunk-flip hysteresis threshold.
    ///   3. <see cref="ChunkSyncClient"/> writes slots when an OWPMOD/Chunks
    ///      CMM message arrives.
    /// On a host, ChunkSyncServer writes (CMM doesn't loop back to the local
    /// sender).  On a pure client, only ChunkSyncClient writes.  On a pure
    /// dedicated server, only ChunkSyncServer writes.
    /// </summary>
    public static class ChunkRegistry
    {
        // 32 × 655 = 20960, integer — chunkSize × ActivePrecision must be an
        // integer for exact round-trip across chunk handoffs.
        public const float ChunkSizeMeters = 32f;

        // Sentinel tickId meaning "no pending switch".  The vanilla server's
        // tick counter wraps from ushort.MaxValue → 0 (see
        // SynchronizedObjectManager.Server_ServerTick at line 197-199), so
        // ushort.MaxValue (0xFFFF) is never a real tickId — safe sentinel.
        // It is also used in the wire format for "apply this chunk
        // immediately, no deferred switch" (bulk snapshot to late joiners).
        public const ushort NoSwitchTickId = ushort.MaxValue;

        public static float ActivePrecision = 655f;
        public static bool ChunksActive;

        // Set per server-tick by ChunkSyncServer.GatherPrefix.  Read by the
        // encode helpers below.
        public static ushort CurrentEncodeTickId;
        // Set per receive-tick by ChunkSyncClient.RpcPrefix.  Read by the
        // decode helpers below.
        public static ushort CurrentDecodeTickId;

        private static readonly Dictionary<ushort, ChunkSlot> _slots
            = new Dictionary<ushort, ChunkSlot>();

        // ──────────────────────────────────────────────────────────────────
        // Mutation — used by ChunkSyncServer / ChunkSyncClient
        // ──────────────────────────────────────────────────────────────────

        public static bool TryGet(ushort id, out ChunkSlot slot) => _slots.TryGetValue(id, out slot);
        public static void Set(ushort id, ChunkSlot slot)         => _slots[id] = slot;
        public static void Remove(ushort id)                      => _slots.Remove(id);
        public static void Clear()                                => _slots.Clear();

        public static IEnumerable<KeyValuePair<ushort, ChunkSlot>> Snapshot() => _slots;
        public static int Count => _slots.Count;

        /// <summary>
        /// Apply an incoming chunk announce (or initial-state entry) to the
        /// per-id slot.  If <paramref name="switchTickId"/> equals
        /// <see cref="NoSwitchTickId"/> the chunk takes effect immediately —
        /// used by the late-join bulk snapshot.  Otherwise we install it as
        /// a deferred Pending; any existing Pending is promoted to Current
        /// first (its switch tick has already passed by definition — the
        /// server only emits a new transition after promoting the previous).
        /// </summary>
        public static void ApplyAnnounce(ushort id, ChunkCoord chunk, ushort switchTickId)
        {
            _slots.TryGetValue(id, out var slot);

            if (switchTickId == NoSwitchTickId)
            {
                // Instant set — wipe any pending and overwrite Current.
                slot.Current = chunk;
                slot.HasPending = false;
                slot.Pending = default;
                slot.PendingTickId = 0;
            }
            else
            {
                // Promote any existing pending into Current before installing
                // the new pending.  See class header on why this is safe.
                if (slot.HasPending)
                    slot.Current = slot.Pending;

                slot.Pending = chunk;
                slot.PendingTickId = switchTickId;
                slot.HasPending = true;
            }

            _slots[id] = slot;
        }

        // ──────────────────────────────────────────────────────────────────
        // Helpers called from transpiled IL.
        //
        // Per-axis encode/decode at ~30 objects × 100 Hz × 4 = 12 k calls/sec.
        // ResolveAt is one comparison + one branch; the dictionary lookup
        // dominates at ~50 ns.  Total <1% CPU at full load.
        // ──────────────────────────────────────────────────────────────────

        public static short EncodeX(float worldX, ushort id)
        {
            if (!ChunksActive)
                return (short)(worldX * ActivePrecision);

            float offset = 0f;
            if (_slots.TryGetValue(id, out var slot))
                offset = slot.ResolveAt(CurrentEncodeTickId).X * ChunkSizeMeters;
            return (short)((worldX - offset) * ActivePrecision);
        }

        public static short EncodeY(float worldY)
        {
            return (short)(worldY * ActivePrecision);
        }

        public static short EncodeZ(float worldZ, ushort id)
        {
            if (!ChunksActive)
                return (short)(worldZ * ActivePrecision);

            float offset = 0f;
            if (_slots.TryGetValue(id, out var slot))
                offset = slot.ResolveAt(CurrentEncodeTickId).Z * ChunkSizeMeters;
            return (short)((worldZ - offset) * ActivePrecision);
        }

        public static float DecodeX(float encoded, ushort id)
        {
            float val = encoded / ActivePrecision;
            if (!ChunksActive) return val;

            if (_slots.TryGetValue(id, out var slot))
                val += slot.ResolveAt(CurrentDecodeTickId).X * ChunkSizeMeters;
            return val;
        }

        public static float DecodeY(float encoded)
        {
            return encoded / ActivePrecision;
        }

        public static float DecodeZ(float encoded, ushort id)
        {
            float val = encoded / ActivePrecision;
            if (!ChunksActive) return val;

            if (_slots.TryGetValue(id, out var slot))
                val += slot.ResolveAt(CurrentDecodeTickId).Z * ChunkSizeMeters;
            return val;
        }
    }

    /// <summary>
    /// Integer 2D chunk index.  ±127 chunks at 32 m gives a ±4 km world span,
    /// way past anything the open world platform reaches today.  Promote to
    /// short if you ever need more.
    /// </summary>
    public struct ChunkCoord
    {
        public sbyte X;
        public sbyte Z;

        public ChunkCoord(sbyte x, sbyte z) { X = x; Z = z; }

        public override string ToString() => $"({X},{Z})";

        public override bool Equals(object obj)
            => obj is ChunkCoord c && c.X == X && c.Z == Z;
        public override int GetHashCode() => (X << 8) | (byte)Z;

        public static bool operator ==(ChunkCoord a, ChunkCoord b) => a.X == b.X && a.Z == b.Z;
        public static bool operator !=(ChunkCoord a, ChunkCoord b) => !(a == b);
    }

    /// <summary>
    /// Per-object resolution state.  Current is the chunk we use for tickIds
    /// strictly before PendingTickId; Pending is what we use for tickIds at
    /// or after PendingTickId (when HasPending is true).  Modular arithmetic
    /// on the ushort tickId makes the comparison wrap-safe — same trick the
    /// vanilla skipLateTicks check at SynchronizedObjectManager.cs:273 uses.
    /// </summary>
    public struct ChunkSlot
    {
        public ChunkCoord Current;
        public ChunkCoord Pending;
        public ushort     PendingTickId;
        public bool       HasPending;

        public ChunkCoord ResolveAt(ushort tickId)
        {
            if (!HasPending) return Current;
            // Modular GE: tickId is at or after PendingTickId.  The window
            // of "at or after" is the front half of the 16-bit space; the
            // back half is "before".  Identical to the skipLateTicks logic.
            return ((ushort)(tickId - PendingTickId) < 32768) ? Pending : Current;
        }
    }
}
