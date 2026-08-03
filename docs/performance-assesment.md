# Performance Assessment

Allocation and memory-traffic accounting for the save pipeline, from `IDataShard` instances to
bytes on the medium.

Figures are **derived analytically from the source**, not profiled. Each one is traceable to a
specific allocation site, so a change that invalidates a number should be visible in review. Treat
them as an upper bound on what the pipeline *asks for*, not as a measurement of what an allocator
or the OS actually commits.

A section is added per release. Within a section, the axis tables explain *where* the cost comes
from and the matrix combines them; the matrix is the sum of its axes and nothing else.

---

## [0.6.0]

Same scenario, symbols and accounting rules as [0.5.0](#050) below — only the pipeline changed.

### What changed

| Change                                                | Removes                                                                        |
|-------------------------------------------------------|--------------------------------------------------------------------------------|
| `ISaveLayout.HeaderReservation` / `BlobReservation`   | the payload copy in **both** layouts, and single-file's second full-size arena |
| `EnvelopeCodec.ExactEncodedSize`                      | the slack that made an exact header reservation impossible                     |
| `BufferWriterTextWriter` (Newtonsoft)                 | 4P of string intermediates and one encode pass                                 |
| `CloudSaveStorage` uses the `Stream` overload         | the `ToArray()` copy and its payload-sized `byte[]`                            |
| Per-slot range buffer reuse                           | 24N per save in steady state                                                   |
| `UnityJsonSerializer` reserves the exact UTF-8 length | the arena doubling and its payload-sized memcpy                                |

`UnityJsonSerializer` keeps its 2P string intermediate and its encode pass: `JsonUtility` returns a
`string` and has no streaming entry point, so a UTF-16 copy of each shard is inherent to the
backend. Everything around it that was avoidable is gone.

### Serializer profiles now

| Serializer                                              | Intermediate   | Arena            | Copies contributed |
|---------------------------------------------------------|----------------|------------------|--------------------|
| System.Text.Json, MessagePack, MemoryPack, protobuf-net | 0              | `P`              | 0                  |
| `NewtonsoftJsonSerializer`                              | **0** (was 4P) | `P` (was 3P)     | **0** (was 2×P)    |
| `UnityJsonSerializer`                                   | 2P             | **`P`** (was 3P) | **1×P** (was 3×P)  |

### Matrix

Twelve distinct profiles; the four buffer-native serializers and Newtonsoft now share one.
"Direct" below means any of those five.

Two ratio columns, both **lower is better**, answering different questions:

- **Allocation Diff** — `Now ÷ 0.5.0`. How much of the previous release's cost survives. Rows with
  two 0.5.0 figures (a direct-serializer row covers both the buffer-native and Newtonsoft profiles,
  which used to differ) carry two ratios in the same order.
- **Floor Allocation Diff** — `Now ÷ Floor`. How far the configuration still is from the
  theoretical minimum. `1.0×` means *at* the minimum, not near it: the value the assessment
  computes as unimprovable for that combination.

Read them together. An absolute figure only says how big the save was; Allocation Diff says what
this release bought, and Floor Allocation Diff says what is left on the table.

| Serializer  | Layout | Storage     | 0.5.0         | Now        | Allocation Diff<br/>(lower is better) | Copies | Floor  | Floor Allocation Diff<br/>(lower is better) |
|-------------|--------|-------------|---------------|------------|:-------------------------------------:|--------|--------|:-------------------------------------------:|
| Direct      | Single | File        | 207 / 807 KB  | **105 KB** |            0.507 / 0.130x             | **0**  | 105 KB |                  **1.0×**                   |
| Direct      | Single | Cloud       | 312 / 912 KB  | **105 KB** |            0.336 / 0.115x             | **0**  | 105 KB |                  **1.0×**                   |
| Direct      | Single | PlayerPrefs | 486 / 1086 KB | **383 KB** |            0.788 / 0.353x             | 1×P    | 383 KB |                  **1.0×**                   |
| Direct      | Multi  | PlayerPrefs | 379 / 979 KB  | **378 KB** |            0.997 / 0.386x             | 1×P    | 277 KB |                    1.4×                     |
| `UnityJson` | Single | PlayerPrefs | 886 KB        | **583 KB** |                0.658x                 | 2×P    | 383 KB |                    1.5×                     |
| `UnityJson` | Multi  | PlayerPrefs | 779 KB        | **578 KB** |                0.742x                 | 2×P    | 277 KB |                    2.1×                     |
| `UnityJson` | Single | File        | 607 KB        | **305 KB** |                0.502x                 | 1×P    | 105 KB |                    2.9×                     |
| `UnityJson` | Single | Cloud       | 712 KB        | **305 KB** |                0.428x                 | 1×P    | 105 KB |                    2.9×                     |
| Direct      | Multi  | File        | 105 / 705 KB  | **103 KB** |            0.981 / 0.146x             | **0**  | 2.2 KB |                     47×                     |
| Direct      | Multi  | Cloud       | 208 / 808 KB  | **103 KB** |            0.495 / 0.127x             | **0**  | 2.2 KB |                     47×                     |
| `UnityJson` | Multi  | File        | 505 KB        | **303 KB** |                 0.6x                  | 1×P    | 2.2 KB |                    138×                     |
| `UnityJson` | Multi  | Cloud       | 608 KB        | **303 KB** |                0.498x                 | 1×P    | 2.2 KB |                    138×                     |

Sorted by **Floor Allocation Diff**, because that ordering is the recommendation: the top three
rows are at the minimum and are the configurations to reach for; the bottom two are the ones to
avoid when allocation matters.

The two columns disagree most where it is worth understanding why:

- **`Direct / Multi / File`** improved barely at all against 0.5.0 on the buffer-native profile
  (0.981×) yet moves zero payload bytes now. Nothing was wasted before *in bytes* — the old scratch
  was already small — so removing the copy shows up as speed, not size.
- The same row is **47× its floor**, the worst gap in the table despite the lowest absolute cost.
  Every one of those kilobytes is the manager's payload arena, which multi-file never reads more
  than one blob of at a time. That is the outstanding work, not a defect in the layout.
- **`UnityJson` rows halved** against 0.5.0 (≈0.5×) but sit at 2.9–138× their floors: the
  reservation fix removed everything avoidable, and what remains is `JsonUtility` returning a
  `string`.

### Reading the numbers

**Every direct-serializer path now moves zero payload bytes** beyond the write to the medium
itself. That was the goal, and it came from one contract change rather than from tuning: the shards
are serialized into their final position, so there is nothing left to move.

**Single-file reaches its floor on all three storages.** The arena *is* the file; the storage write
is handed the same memory the serializer wrote into. The earlier claim that the envelope-before-
payload ordering forced a copy was wrong — it only forced the envelope's size to be known exactly,
which `ExactEncodedSize` supplies.

**Newtonsoft went from the most expensive serializer to tied for cheapest**, an 807 KB → 105 KB
drop on the single-file/file path. Nothing about the JSON changed; the string simply stopped
existing.

**Multi-file is still 47× its floor,** and deliberately so. Its allocation is now exactly the save
size, but the manager's P-sized arena remains — the layout never needs more than one blob at a
time, and removing that needs layout-driven serialization, which is out of scope here.

**`UnityJson` halved without changing `JsonUtility`.** Reserving the exact UTF-8 length instead of
three bytes per char removed the arena doubling and the payload-sized memcpy it dragged along —
607 → 305 KB, copies 3×P → 1×P. What is left is the 2P UTF-16 string and the single encode pass
into the arena, both inherent to a backend that returns a `string`. It is now within 2.9× of the
single-file floor, against 5.8× before.

**Every remaining copy in the package is now either a format transform or a backend's API.**
`UnityJson`'s encode, PlayerPrefs' base64 — nothing left is the pipeline moving bytes for its own
convenience.

---

## 0.5.0

### What is counted

Counted:

- every `NativeArray` / `NativeList` allocation in the pipeline,
- every `ArrayPool` rental (pooled memory is still resident),
- serializer intermediates that scale with payload size,
- the base64 `string` `PlayerPrefsStorage` builds, at **2 bytes per char** (UTF-16).

Not counted: storage keys, task and closure objects, `IBufferWriter` instances, and other
fixed-size bookkeeping that does not scale with save size.

Compression and encryption are excluded — `TransformStorage` and its chain are out of scope here,
since their cost is a function of the transform rather than of the pipeline.

### Scenario and symbols

Steady state, meaning the arena size hint equals the previous save's payload. **All `N` shards are
dirty**, so single-file and multi-file move comparable work.

| Symbol | Meaning                                                       | Worked example |
|--------|---------------------------------------------------------------|----------------|
| `N`    | shard count                                                   | 100            |
| `b`    | per-shard blob size                                           | 1 024 B        |
| `P`    | payload, `N·b`                                                | 102 400 B      |
| `E`    | envelope, `32 + T + 20N`                                      | 2 232 B        |
| `E⁺`   | `EnvelopeCodec.MaxEncodedSize` bound (3 B/char on type names) | 2 416 B        |
| `R`    | ranges array, `24N`                                           | 2 400 B        |
| `S₁`   | single-file save size, `E + 8 + 24N + P`                      | 107 040 B      |
| `S_M`  | multi-file total, `E + 8N + P` across `N+1` files             | 105 432 B      |

### Cost by serializer

| Serializer                   | Path to bytes                           | Intermediate                   | Effect on the arena |
|------------------------------|-----------------------------------------|--------------------------------|---------------------|
| `UnityJsonSerializer`        | `JsonUtility.ToJson` → `string` → UTF-8 | **2P**                         | forces a doubling   |
| `NewtonsoftJsonSerializer`   | `StringBuilder` → `string` → UTF-8      | **4P** (2P chunks + 2P string) | forces a doubling   |
| `SystemTextJsonSerializer`   | `Utf8JsonWriter(writer)`                | 0                              | none                |
| `MessagePackShardSerializer` | `Serialize(…, writer, …)`               | 0                              | none                |
| `MemoryPackShardSerializer`  | `Serialize(type, in writer, …)`         | 0                              | none                |
| `ProtobufNetSerializer`      | `ProtoWriter.State.Create(writer, …)`   | 0                              | none                |

**The arena doubling is derived, not observed.** `NativeListBufferWriter.EnsureFreeCapacity` grows
when `length + sizeHint > capacity`. Both JSON serializers reserve three bytes per UTF-16 char
(`json.Length * 3`, `GetMaxByteCount`), so serializing the last shard demands
`(P − b) + 3b = P + 2b`, which exceeds the pre-sized capacity `P`. Capacity goes to `2P` and
`SetCapacity` memcpys everything written so far. Arena cost becomes `P + 2P = 3P` allocated, plus
one extra P-sized copy that appears nowhere in the source.

Buffer-native serializers request what they intend to write, so the pre-sized arena holds and the
arena stays at `P`.

### Cost by layout

| Layout                 | Buffer                                        | Payload copies               | Example   |
|------------------------|-----------------------------------------------|------------------------------|-----------|
| `SingleFileSaveLayout` | `E⁺ + 8 + 24N + P` — a second full-size arena | 1×P (`Pack` memcpy)          | 107 224 B |
| `MultiFileSaveLayout`  | `max(8 + b, E⁺, 256)` — one file at a time    | 1×P (per blob, into scratch) | 2 416 B   |

Both move the same total bytes. They differ ~44× in peak buffer at this shard count.

### Cost by storage

| Storage              | Allocation                     | Copies | Mechanism                                                                      |
|----------------------|--------------------------------|--------|--------------------------------------------------------------------------------|
| `FileStorage`        | **0**                          | **0**  | `stream.Write(ReadOnlySpan<byte>)` over the unmanaged pointer, `bufferSize: 1` |
| `PlayerPrefsStorage` | `2·⌈len/3⌉·4` ≈ **2.67 × len** | 1×P    | base64 held as a UTF-16 `string`                                               |
| `CloudSaveStorage`   | **1 × len**                    | 1×P    | `data.ToArray()`; the UGS API takes `byte[]`                                   |

### Full matrix

`Allocation = serializer + arena + ranges + layout + storage`. The example column is total bytes
allocated over one save at N = 100 × 1 KB. Copies are payload-proportional memcpys, in multiples
of `P`.

| #  | Serializer       | Layout | Storage     | Actual allocation              | @ example | Copies | Min. allocation | Min. copies | Floor allocation diff<br/>(lower is better) |
|----|------------------|--------|-------------|--------------------------------|-----------|--------|-----------------|-------------|:-------------------------------------------:|
| 1  | UnityJson        | Single | File        | `2P + 3P + 24N + (E⁺+8+24N+P)` | 607 KB    | 3×P    | 105 KB          | 0           |                    5.78x                    |
| 2  | UnityJson        | Single | PlayerPrefs | + `2.67·S₁`                    | 886 KB    | 4×P    | 383 KB          | 1×P         |                    2.31x                    |
| 3  | UnityJson        | Single | Cloud       | + `S₁`                         | 712 KB    | 4×P    | 209 KB          | 1×P         |                    3.41x                    |
| 4  | UnityJson        | Multi  | File        | `2P + 3P + 24N + max(8+b, E⁺)` | 505 KB    | 3×P    | 2.2 KB          | 0           |                   229.5x                    |
| 5  | UnityJson        | Multi  | PlayerPrefs | + `2.67·S_M`                   | 779 KB    | 4×P    | 277 KB          | 1×P         |                    2.81x                    |
| 6  | UnityJson        | Multi  | Cloud       | + `S_M`                        | 608 KB    | 4×P    | 105 KB          | 1×P         |                    5.79x                    |
| 7  | Newtonsoft       | Single | File        | `4P + 3P + 24N + (E⁺+8+24N+P)` | 807 KB    | 4×P    | 105 KB          | 0           |                    7.69x                    |
| 8  | Newtonsoft       | Single | PlayerPrefs | + `2.67·S₁`                    | 1 086 KB  | 5×P    | 383 KB          | 1×P         |                    2.83x                    |
| 9  | Newtonsoft       | Single | Cloud       | + `S₁`                         | 912 KB    | 5×P    | 209 KB          | 1×P         |                    4.36x                    |
| 10 | Newtonsoft       | Multi  | File        | `4P + 3P + 24N + max(8+b, E⁺)` | 705 KB    | 4×P    | 2.2 KB          | 0           |                   320.4x                    |
| 11 | Newtonsoft       | Multi  | PlayerPrefs | + `2.67·S_M`                   | 979 KB    | 5×P    | 277 KB          | 1×P         |                    3.53x                    |
| 12 | Newtonsoft       | Multi  | Cloud       | + `S_M`                        | 808 KB    | 5×P    | 105 KB          | 1×P         |                    7.69x                    |
| 13 | System.Text.Json | Single | File        | `P + 24N + (E⁺+8+24N+P)`       | 207 KB    | 1×P    | 105 KB          | 0           |                    1.97x                    |
| 14 | System.Text.Json | Single | PlayerPrefs | + `2.67·S₁`                    | 486 KB    | 2×P    | 383 KB          | 1×P         |                    1.27x                    |
| 15 | System.Text.Json | Single | Cloud       | + `S₁`                         | 312 KB    | 2×P    | 209 KB          | 1×P         |                    1.49x                    |
| 16 | System.Text.Json | Multi  | File        | `P + 24N + max(8+b, E⁺)`       | 105 KB    | 1×P    | 2.2 KB          | 0           |                   47.73x                    |
| 17 | System.Text.Json | Multi  | PlayerPrefs | + `2.67·S_M`                   | 379 KB    | 2×P    | 277 KB          | 1×P         |                    1.39x                    |
| 18 | System.Text.Json | Multi  | Cloud       | + `S_M`                        | 208 KB    | 2×P    | 105 KB          | 1×P         |                    1.98x                    |
| 19 | MessagePack      | Single | File        | as #13                         | 207 KB    | 1×P    | 105 KB          | 0           |                    1.97x                    |
| 20 | MessagePack      | Single | PlayerPrefs | as #14                         | 486 KB    | 2×P    | 383 KB          | 1×P         |                    1.27x                    |
| 21 | MessagePack      | Single | Cloud       | as #15                         | 312 KB    | 2×P    | 209 KB          | 1×P         |                    1.49x                    |
| 22 | MessagePack      | Multi  | File        | as #16                         | 105 KB    | 1×P    | 2.2 KB          | 0           |                   74.73x                    |
| 23 | MessagePack      | Multi  | PlayerPrefs | as #17                         | 379 KB    | 2×P    | 277 KB          | 1×P         |                    1.39x                    |
| 24 | MessagePack      | Multi  | Cloud       | as #18                         | 208 KB    | 2×P    | 105 KB          | 1×P         |                    1.98x                    |
| 25 | MemoryPack       | Single | File        | as #13                         | 207 KB    | 1×P    | 105 KB          | 0           |                    1.97x                    |
| 26 | MemoryPack       | Single | PlayerPrefs | as #14                         | 486 KB    | 2×P    | 383 KB          | 1×P         |                    1.27x                    |
| 27 | MemoryPack       | Single | Cloud       | as #15                         | 312 KB    | 2×P    | 209 KB          | 1×P         |                    1.49x                    |
| 28 | MemoryPack       | Multi  | File        | as #16                         | 105 KB    | 1×P    | 2.2 KB          | 0           |                   74.73x                    |
| 29 | MemoryPack       | Multi  | PlayerPrefs | as #17                         | 379 KB    | 2×P    | 277 KB          | 1×P         |                    1.39x                    |
| 30 | MemoryPack       | Multi  | Cloud       | as #18                         | 208 KB    | 2×P    | 105 KB          | 1×P         |                    1.98x                    |
| 31 | protobuf-net     | Single | File        | as #13                         | 207 KB    | 1×P    | 105 KB          | 0           |                    1.97x                    |
| 32 | protobuf-net     | Single | PlayerPrefs | as #14                         | 486 KB    | 2×P    | 383 KB          | 1×P         |                    1.27x                    |
| 33 | protobuf-net     | Single | Cloud       | as #15                         | 312 KB    | 2×P    | 209 KB          | 1×P         |                    1.49x                    |
| 34 | protobuf-net     | Multi  | File        | as #16                         | 105 KB    | 1×P    | 2.2 KB          | 0           |                   74.73x                    |
| 35 | protobuf-net     | Multi  | PlayerPrefs | as #17                         | 379 KB    | 2×P    | 277 KB          | 1×P         |                    1.39x                    |
| 36 | protobuf-net     | Multi  | Cloud       | as #18                         | 208 KB    | 2×P    | 105 KB          | 1×P         |                    1.98x                    |

The 36 rows collapse to **12 distinct profiles**: the four buffer-native serializers are
byte-identical to each other.

### Gap to the floor

**The best row sits at 47× its floor; the worst at 493×.** Row 28 — MemoryPack, multi-file,
`FileStorage` — allocates 105 KB against a 2.2 KB floor. The gap is almost entirely the manager's
P-sized arena, which **multi-file does not structurally need**: it serializes into a P-sized arena
and then copies each blob into a max-blob scratch. Serializing each shard directly into that
scratch would remove the arena, the ranges array and the 1×P copy together.

**Single-file's floor is `S₁`, and it is reachable.** `GetOrBuildEnvelope` runs *before*
`_pipeline.SaveAsync`, so `E` and `24N` are both known before a byte is serialized. One buffer of
`E + 8 + 24N + P`, with the envelope and ranges written first and shards serialized straight into
the tail, takes row 13 from 207 KB to 105 KB and its copies from 1×P to zero. The `Pack` memcpy is
structural only if the envelope size is unknown up front — and it is not.

**The JSON serializers cost 3–4× the buffer-native ones**, and mostly not for the strings. The
three-bytes-per-char reservation doubles the arena on *every* save — 2P wasted plus a P-sized
memcpy — and nothing at the call site suggests it. Calling `GetByteCount` first, or reserving
`json.Length` and letting the writer grow, removes it.

**`FileStorage` is the only backend with a zero floor, and it reaches it.** PlayerPrefs cannot beat
2.67× the file size in UTF-16 base64; that is `PlayerPrefs.SetString`, not this package.
CloudSave's `ToArray()` is forced by UGS accepting only `byte[]`.

**Peak and churn diverge for multi-file + PlayerPrefs.** The 379 KB in row 29 is total churn across
101 transient base64 strings; peak resident is about 4.9 KB. Single-file produces one 279 KB string,
where peak and churn coincide — which is also what the iOS/tvOS value-budget check measures.
