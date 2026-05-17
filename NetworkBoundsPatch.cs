using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace MyPuckMod.Net
{
    /// <summary>
    /// Extends Puck's network position quantisation so the open-world platform
    /// reaches arbitrary distances from the rink without losing precision.
    ///
    /// Vanilla Puck quantises positions through SynchronizedObjectManager:
    ///     short encoded = (short)(position * 655f);
    /// shorts are signed 16-bit so the maximum representable position is
    ///     32767 / 655 ≈ 50 m
    /// We Harmony-prefix both <c>EncodeSynchronizedObject</c> and
    /// <c>DecodeSynchronizedObjectData</c>, write our own quantisation into
    /// <c>__result</c>, and return false to skip the original body entirely.
    /// Our prefix calls <see cref="ChunkRegistry"/>'s axis helpers, which
    /// subtract / add a per-object chunk offset before / after the 16-bit
    /// clamp.  Range becomes unlimited (well, ±4 km at chunk-index sbyte
    /// resolution) at vanilla 1.5 mm precision.
    ///
    /// CompetitiveAdjustments coexistence: that mod transpiles the same two
    /// methods to swap 655f for its own field.  Because our prefix returns
    /// false, the (transpiled) original body never executes — their IL
    /// changes become inert.  We still mirror our chosen precision into
    /// their <c>DashFallMod.ArenaBoundsHelper.ActivePrecision</c> field so
    /// any unrelated code in their mod that reads the constant sees a
    /// consistent value.
    /// </summary>
    public static class NetworkBoundsPatch
    {
        // Vanilla constant in SynchronizedObjectManager.  The chunked mode
        // keeps this value — the whole point of chunking is to retain the
        // 1.5 mm grid that 655 gives you.
        private const float VANILLA_PRECISION = 655f;

        // Master switch — true once <see cref="EnableOpenWorldPrecision"/>
        // has flipped chunking on.  Exposed publicly so call sites can
        // branch on it (currently nothing reads it externally — kept as a
        // hook for future toggles).
        public static bool ChunksEnabled => ChunkRegistry.ChunksActive;

        private static Harmony _harmony;
        private static bool _patched;

        // Reflection cache for the CompetitiveAdjustments field.
        private static FieldInfo _compAdjustPrecisionField;
        private static bool _compAdjustLookupDone;

        // ──────────────────────────────────────────────────────────────────
        // Public API
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Install the Harmony prefixes.  Safe to call repeatedly.  Should be
        /// called from both server and client because encode/decode share a
        /// single implementation — if either side runs vanilla / a different
        /// mod's quantiser while the other runs patched, every networked
        /// position will appear scaled or offset wrong.
        /// </summary>
        public static void EnsurePatched()
        {
            if (_patched) return;

            try
            {
                if (_harmony == null)
                    _harmony = new Harmony("openworldpractice.networkbounds");

                var encode = AccessTools.Method(typeof(SynchronizedObjectManager), "EncodeSynchronizedObject");
                var decode = AccessTools.Method(typeof(SynchronizedObjectManager), "DecodeSynchronizedObjectData");

                if (encode == null || decode == null)
                {
                    Debug.LogWarning("[OWP] Could not find SynchronizedObjectManager encode/decode methods — chunked sync disabled.");
                    return;
                }

                _harmony.Patch(encode,
                    prefix: new HarmonyMethod(typeof(NetworkBoundsPatch), nameof(EncodePrefix)));
                _harmony.Patch(decode,
                    prefix: new HarmonyMethod(typeof(NetworkBoundsPatch), nameof(DecodePrefix)));

                _patched = true;
                Debug.Log("[OWP] NetworkBoundsPatch: full-replacement prefixes installed on EncodeSynchronizedObject / DecodeSynchronizedObjectData.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[OWP] Failed to apply network bounds patches: " + ex.Message);
            }
        }

        /// <summary>
        /// Activate chunked sync mode.  Precision stays at 655 (the chunked
        /// mode's whole purpose is to keep vanilla precision), per-object
        /// offsets become live, and the server-side hysteresis sweep starts
        /// announcing chunk changes.  Idempotent.
        ///
        /// CompetitiveAdjustments coexistence is transparent: our prefix
        /// returns false so their transpiled body never runs, and we mirror
        /// 655 into their precision field so any unrelated code reading that
        /// constant in their mod stays consistent.
        /// </summary>
        public static void EnableOpenWorldPrecision()
        {
            EnsurePatched();

            ChunkRegistry.ActivePrecision = VANILLA_PRECISION;
            ChunkRegistry.ChunksActive = true;
            MirrorIntoCompAdjust(VANILLA_PRECISION);

            ChunkSyncServer.Enable();
            ChunkSyncClient.Enable();

            Debug.Log("[OWP] Chunked network sync ACTIVE — precision " + VANILLA_PRECISION + " (1.5 mm grid), unlimited range.");
        }

        /// <summary>Returns to vanilla precision with no chunking — used when leaving the open world.</summary>
        public static void RestoreVanillaPrecision()
        {
            ChunkSyncServer.Disable();
            ChunkSyncClient.Disable();
            ChunkRegistry.ChunksActive = false;
            ChunkRegistry.Clear();
            ChunkRegistry.ActivePrecision = VANILLA_PRECISION;
            MirrorIntoCompAdjust(VANILLA_PRECISION);
            Debug.Log("[OWP] Network precision restored to vanilla " + VANILLA_PRECISION + " (chunks disabled).");
        }

        /// <summary>Full teardown — unpatches Harmony and clears all state.</summary>
        public static void Disable()
        {
            ChunkSyncServer.Disable();
            ChunkSyncClient.Disable();
            ChunkRegistry.ChunksActive = false;
            ChunkRegistry.Clear();
            ChunkRegistry.ActivePrecision = VANILLA_PRECISION;
            MirrorIntoCompAdjust(VANILLA_PRECISION);

            if (_harmony != null)
            {
                try { _harmony.UnpatchSelf(); }
                catch (Exception ex) { Debug.LogWarning("[OWP] UnpatchSelf failed: " + ex.Message); }
                _harmony = null;
            }

            _patched = false;
            Debug.Log("[OWP] NetworkBoundsPatch fully disabled.");
        }

        // ──────────────────────────────────────────────────────────────────
        // Harmony prefixes — full-replacement encode / decode.
        //
        // Both methods are pure transforms with no side effects in the
        // SynchronizedObjectManager itself.  We can replace them outright by
        // writing __result and returning false; any other mod's transpiler
        // on these methods becomes dead code.
        //
        // Rotation is quantised independently of position with a different
        // constant (32767f) — we replicate that math verbatim.  Position
        // goes through the chunk helpers so offsets are applied per axis.
        // ──────────────────────────────────────────────────────────────────

        // ReSharper disable once UnusedMember.Global
        public static bool EncodePrefix(
            ulong networkObjectId,
            Vector3 position,
            Quaternion rotation,
            ref System.ValueTuple<ushort, short[], short[]> __result)
        {
            ushort id = (ushort)networkObjectId;

            short rx = (short)(rotation.x * 32767f);
            short ry = (short)(rotation.y * 32767f);
            short rz = (short)(rotation.z * 32767f);
            short rw = (short)(rotation.w * 32767f);

            short px = ChunkRegistry.EncodeX(position.x, id);
            short py = ChunkRegistry.EncodeY(position.y);
            short pz = ChunkRegistry.EncodeZ(position.z, id);

            __result = new System.ValueTuple<ushort, short[], short[]>(
                id,
                new short[] { px, py, pz },
                new short[] { rx, ry, rz, rw });

            return false; // skip the original (possibly transpiled) body
        }

        // ReSharper disable once UnusedMember.Global
        public static bool DecodePrefix(
            SynchronizedObjectData synchronizedObjectData,
            ref System.ValueTuple<ushort, Vector3, Quaternion> __result)
        {
            ushort id = synchronizedObjectData.NetworkObjectId;

            float rx = synchronizedObjectData.Rx / 32767f;
            float ry = synchronizedObjectData.Ry / 32767f;
            float rz = synchronizedObjectData.Rz / 32767f;
            float rw = synchronizedObjectData.Rw / 32767f;

            float px = ChunkRegistry.DecodeX(synchronizedObjectData.X, id);
            float py = ChunkRegistry.DecodeY(synchronizedObjectData.Y);
            float pz = ChunkRegistry.DecodeZ(synchronizedObjectData.Z, id);

            __result = new System.ValueTuple<ushort, Vector3, Quaternion>(
                id,
                new Vector3(px, py, pz),
                new Quaternion(rx, ry, rz, rw));

            return false;
        }

        // ──────────────────────────────────────────────────────────────────
        // CompetitiveAdjustments interop
        // ──────────────────────────────────────────────────────────────────

        private static void MirrorIntoCompAdjust(float value)
        {
            ResolveCompAdjustField();
            if (_compAdjustPrecisionField == null) return;

            try
            {
                _compAdjustPrecisionField.SetValue(null, value);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[OWP] Failed to mirror precision into CompetitiveAdjustments: " + ex.Message);
            }
        }

        private static void ResolveCompAdjustField()
        {
            if (_compAdjustLookupDone) return;
            _compAdjustLookupDone = true;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t;
                try { t = asm.GetType("DashFallMod.ArenaBoundsHelper", false); }
                catch { continue; }

                if (t == null) continue;
                _compAdjustPrecisionField = t.GetField(
                    "ActivePrecision",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (_compAdjustPrecisionField != null)
                {
                    Debug.Log("[OWP] Detected CompetitiveAdjustments — will keep its precision in sync.");
                    return;
                }
            }
        }
    }
}
