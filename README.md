# Chunked Network Sync — design notes

This folder is a self-contained record of the chunked-sync system that lets `OpenWorldPracticeMod` extend Puck's networked position range beyond the vanilla ~50 m wrap-around while keeping vanilla 1.5 mm precision. The four source files live in `src/Net/` in the main project; copies are duplicated here so the writeup and the code travel together.

## The problem

Puck's `SynchronizedObjectManager` quantises every networked position to a signed 16-bit short:

```csharp
short encoded = (short)(position * 655f);   // encode
float decoded = encoded / 655f;             // decode
```

Range × precision is fixed at `32767`. So the trade is:

| precision | range  | grid step |
|-----------|--------|-----------|
| 655       | ±50 m  | 1.5 mm    |
| 300       | ±109 m | 3.3 mm    |
| 100       | ±327 m | 10.0 mm   |
| 75        | ±437 m | 13.3 mm   |

The mod's previous approach (and CompetitiveAdjustments') was: drop `655f` to `75f`, get ±437 m of range, and tween away the resulting 13 mm jitter on the client. That worked, but on stationary or slow-moving objects the grid steps were still perceptible.

**Switching to a 32-bit wire format was off the table** — it would break compatibility with vanilla clients and CompetitiveAdjustments.

## The idea

Treat the world as a grid of 32 m × 32 m chunks on X / Z (Y is not chunked). Each networked object is assigned a current chunk. Encoding subtracts the chunk's origin from the world position; decoding adds it back. The wire value stays within ±50 m of zero, so the 16-bit short never overflows and we keep the vanilla 1.5 mm grid.

```
worldX  = encodedX / 655f + chunkX × 32 m
encodedX = (worldX − chunkX × 32 m) × 655f
```

`32 × 655 = 20960` is integer, so a chunk handoff is bit-exact.

## The architecture

Four files, ~700 lines total.

```
NetworkBoundsPatch    Harmony prefix on Encode/DecodeSynchronizedObject
                     + CompAdjust mirror + enable/disable orchestration
       │
       ▼  calls
ChunkRegistry         Per-id ChunkSlot table + axis encode/decode helpers
       ▲                                       ▲
       │ writes (server)                       │ writes (client)
       │                                       │
ChunkSyncServer ─── OWPMOD/Chunks reliable RPC ─── ChunkSyncClient
                                                       │
                                                       └─ reject filter
                                                          on OnClientTick /
                                                          OnClientSmoothTick
```

### Why a Harmony prefix and not a transpiler

The first iteration used a transpiler that pattern-matched the `ldc.r4 655` constants in the encode / decode IL and rewrote the surrounding instructions. Two problems:

1. **CompetitiveAdjustments also transpiles those methods.** If their patch runs first, the `ldc.r4 655` we look for has already been swapped for an `ldsfld` of their precision field. Our transpiler then finds nothing to replace.
2. **The IL pattern is fragile.** Any other mod or future game update that touches the encode / decode methods can break us silently.

The current implementation is a Harmony **prefix** that fully replaces the method body — we compute the result, write it to `__result`, and `return false` to skip the original. Any IL another mod has injected into those methods becomes dead code. Our prefix is just regular C# and is trivial to read.

```csharp
public static bool EncodePrefix(
    ulong networkObjectId, Vector3 position, Quaternion rotation,
    ref System.ValueTuple<ushort, short[], short[]> __result)
{
    ushort id = (ushort)networkObjectId;
    short rx = (short)(rotation.x * 32767f);    // rotation: verbatim
    short ry = (short)(rotation.y * 32767f);
    short rz = (short)(rotation.z * 32767f);
    short rw = (short)(rotation.w * 32767f);
    short px = ChunkRegistry.EncodeX(position.x, id);   // position: chunked
    short py = ChunkRegistry.EncodeY(position.y);
    short pz = ChunkRegistry.EncodeZ(position.z, id);
    __result = new System.ValueTuple<ushort, short[], short[]>(
        id, new short[] { px, py, pz }, new short[] { rx, ry, rz, rw });
    return false;
}
```

## The deferred handoff

The hardest design problem isn't the encoding — it's the handoff. When an object crosses from one chunk to the next, the server has to switch its encoding offset and tell every client to switch their decoding offset. If those two events aren't perfectly synchronised, packets in flight during the transition will decode at the wrong offset and the object visibly snaps by 32 m.

The position broadcast is `RpcDelivery.Unreliable`. The chunk announce is `NetworkDelivery.Reliable`. They travel on different Unity Transport pipelines with no cross-channel ordering guarantee — unreliable typically arrives slightly **earlier** because reliable carries acknowledgment overhead.

The fix: **announce the switch in advance and apply it at a specific tickId on both ends.**

1. Server detects a chunk crossing at tick `T`.
2. Server broadcasts `(id, newChunk, switchTick = T + 50)` via the reliable channel.
3. Server keeps encoding with the *old* chunk for ticks `T..T+49` — the old chunk's ±50 m wire range easily contains the object's drift during the defer window (50 ticks @ 100 Hz = 500 ms; a 15 m/s skate covers ~7.5 m).
4. At tick `T+50`, both server (encode) and client (decode) start using the new chunk.

The server's tickId is `SynchronizedObjectManager.serverLastSentTickId` — already incremented inside `Server_ServerTick` before the gather call, where our hysteresis-sweep prefix runs.

The client's tickId is the `tickId` parameter of `Server_SynchronizeObjectsRpc`, which we capture in a Harmony prefix and stash on `ChunkRegistry.CurrentDecodeTickId` for the decode helpers to read.

Both sides resolve each slot the same way:

```csharp
public ChunkCoord ResolveAt(ushort tickId) {
    if (!HasPending) return Current;
    // Modular GE: tickId is at or after PendingTickId, wrap-safe.
    return ((ushort)(tickId - PendingTickId) < 32768) ? Pending : Current;
}
```

So even if the announce arrives early on a fast client and late on a slow one, both decode every packet correctly as long as the announce lands before its `switchTick`. 500 ms of head start beats any realistic RTT.

The sentinel `PendingTickId = 0xFFFF` means "apply immediately" — used by the late-join bulk snapshot, where there's nothing to defer.

## Hysteresis

Naive chunk assignment flaps at the seam: if the player skates along x = 16 m (the wall between chunks 0 and 1), every tiny vibration crosses the boundary and triggers a fresh announce.

We use an 8 m deadband:

- Flip out of the current chunk when you're more than **20 m** from its centre.
- The new chunk's centre is 32 m away, so you land 12 m from the new centre after flipping.
- To flip back, you'd need to drop below 12 m from the new centre — 8 m of inbound travel from where you flipped out.

Per-axis: a diagonal crossing can flip both X and Z in one announce.

## The reject filter

If the reliable announce is somehow delayed past its `switchTick` (extreme jitter, packet loss + retransmit), the client may decode a packet with the wrong chunk. Worst case is a 32 m position discontinuity.

`ChunkSyncClient` filters incoming positions in a Harmony prefix on both `OnClientTick` and `OnClientSmoothTick`. If the decoded position differs from the last accepted position by more than **16 m** on X or Z, we *replace* `position` with the last-good value — the original method then writes the unchanged transform position (a one-tick no-op).

To prevent freezing forever on a legitimately-large move (teleport, missing announce that never recovers), we cap drops at **5 consecutive ticks** (~50 ms @ 100 Hz). After the cap, we accept the next packet whatever it says.

There's also a "no slot yet" branch: before a chunk announce has arrived for an id, we can't trust any decoded position. We pin position to the current transform value until the announce lands and seeds the filter's baseline to the chunk centre. This avoids a teleport-on-first-packet glitch when a fresh client connects to a host whose avatars are at distance.

## Late join

The `SynchronizedObjectManager` already has a "send everything to this client" code path: `Server_ForceSynchronizeClientId`, called when a client finishes scene sync. We Harmony-prefix that method and, before the original runs, push a bulk `OWPMOD/Chunks` snapshot of every known chunk slot to the joining client. Reliable. Type byte 1 ("bulk"), then `count × (id, X, Z, switchTick=0xFFFF)` — instant-apply, no deferred state.

If the reliable bulk lands after the first few unreliable position packets, the filter's "no slot yet" branch holds the avatars at their spawn positions until the slots arrive, then the chunk-centre seeded filter accepts the first packet without a teleport.

## CompetitiveAdjustments coexistence

CompetitiveAdjustments transpiles the same two methods. With our prefix returning false, the (transpiled) original body never executes — CompAdjust's IL changes become inert. We keep the historical mirror behaviour (`MirrorIntoCompAdjust(655f)`) so any *other* code in their mod that reads `DashFallMod.ArenaBoundsHelper.ActivePrecision` sees a consistent value.

This is the simplest possible coexistence: we own encode / decode, they keep their config, no negotiation.

## Tuning constants

All in one place at the top of each file:

| File | Constant | Value | Rationale |
|---|---|---|---|
| `ChunkRegistry.cs` | `ChunkSizeMeters` | `32 m` | `32 × 655 = 20960`, integer round-trip; ±50 m wire range gives ample headroom |
| `ChunkSyncServer.cs` | `FlipOutMeters` | `20 m` | 4 m past `chunkSize/2`; combined with new-chunk geometry gives 8 m deadband |
| `ChunkSyncServer.cs` | `DeferTicks` | `50` | ~500 ms @ 100 Hz; safely beats any realistic announce RTT |
| `ChunkSyncClient.cs` | `RejectThresholdMeters` | `16 m` | Catches a 32 m chunk-mismatch with margin, won't trip on real motion |
| `ChunkSyncClient.cs` | `MaxConsecutiveDrops` | `5` | ~50 ms ceiling on filter freeze; teleports eventually land |

## Honest tradeoffs vs. the old approach

The 75f-with-NetworkJitterFix approach worked. It was ~80 lines of code (just the transpiler + a tween postfix), no per-object state, no RPC. The chunk system is ~700 lines, a per-id state table on both server and client, and a reliable RPC channel.

What we get for the extra lines:

- **1.5 mm grid** (vanilla) instead of **13 mm grid** at the 75f wide-precision setting. The old 13 mm jitter is masked by the tween but still visible on slow / stationary objects to a sharp eye.
- **Unlimited range** (±4 km at sbyte chunk indices) instead of ±437 m.
- **No client-side smoothing layer** — the network sync is identical to vanilla rink play, so anything that worked on the rink works the same way at distance.

What we give up:

- Complexity. The deferred handoff with tickId synchronisation took several iterations to get right; the reject filter is a non-obvious safety net.
- Per-object state and an RPC channel where there used to be none. Network overhead is trivial (a chunk announce is 8 bytes, fires every ~3 seconds per moving object at full sprint), but the table maintenance adds code paths.

## Activation lifecycle

```
Mod.OnEnable
   │
   ├─ creates the controller GameObject
   ├─ initialises PerPlayerPuckManager
   └─ (waits for connection)

ModPresence.Activate                          ← fires on server/host start, or
   │                                            on receipt of an OWPMOD/Hello
   ├─ ...other client patches
   └─ NetworkBoundsPatch.EnsurePatched        ← installs the prefixes only;
                                                ChunkRegistry.ChunksActive
                                                still false, helpers behave
                                                like vanilla 655f

(scene load → AutoSpawnOpenWorld)

NetworkBoundsPatch.EnableOpenWorldPrecision   ← actually flips chunks on
   │
   ├─ ChunkRegistry.ChunksActive = true
   ├─ ChunkRegistry.ActivePrecision = 655
   ├─ MirrorIntoCompAdjust(655)
   ├─ ChunkSyncServer.Enable                  ← server-only does anything
   └─ ChunkSyncClient.Enable

TeardownOpenWorldInstance                     ← /rinkreturn or mod disable
   │
   └─ NetworkBoundsPatch.RestoreVanillaPrecision (chunks off, table cleared)

Mod.OnDisable
   │
   └─ NetworkBoundsPatch.Disable              ← unpatches Harmony
```

## Files in this folder

- [README.md](README.md) — this document
- [NetworkBoundsPatch.cs](NetworkBoundsPatch.cs) — Harmony prefix orchestration
- [ChunkRegistry.cs](ChunkRegistry.cs) — slot table + axis helpers
- [ChunkSyncServer.cs](ChunkSyncServer.cs) — hysteresis sweep + broadcasts
- [ChunkSyncClient.cs](ChunkSyncClient.cs) — receive handler + reject filter

These are reference copies. The live source under `src/Net/` is the canonical version.
