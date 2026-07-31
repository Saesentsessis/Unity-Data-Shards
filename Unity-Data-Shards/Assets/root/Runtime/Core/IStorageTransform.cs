using System;
using System.Buffers;

namespace Saesentsessis.Persistence.Core
{
	/// <summary>
	/// A reversible byte-level transform (compression, encryption, ...) applied at the storage
	/// boundary by <c>TransformStorage</c>.
	/// <para>
	/// The unit of work is <b>one storage key</b>, not one save. A single-file layout writes a whole
	/// save under one key, so the transform sees the entire packed buffer; a multi-file layout writes
	/// the envelope plus one key per shard, so the transform runs once per file and sees an
	/// individual shard blob. Implementations must therefore not assume the bytes handed to them are
	/// a complete, parseable save.
	/// </para>
	/// <para>
	/// The contract is reversibility, not purity: <see cref="Reverse"/> must reconstruct the exact
	/// input of <see cref="Apply"/> for every input. <see cref="Apply"/> itself need not be
	/// deterministic — an encrypting transform is expected to emit a fresh random IV per call, so the
	/// same input legitimately produces different output each time. Implementations carry no state
	/// <i>between</i> calls, but may hold scratch buffers for the duration of one — which is why
	/// they need not be thread-safe.
	/// </para>
	/// <para>
	/// <b>An instance belongs to exactly one <c>TransformStorage</c>, which disposes it.</b> Sharing
	/// one between two chains is not supported: the scratch state above is per-operation, and two
	/// storages driving the same instance would interleave through it. Build a fresh transform per
	/// storage — it also makes disposal unambiguous, since there is only ever one owner.
	/// </para>
	/// </summary>
	public interface IStorageTransform
	{
		/// <summary>Save direction: transforms src and appends the result to dst.</summary>
		/// <remarks>
		/// <b>Must write.</b> For a non-empty <paramref name="src"/> an implementation has to append
		/// something to <paramref name="dst"/> — the chain feeds each step's output into the next, so
		/// a step that writes nothing truncates everything after it and the save is silently lost.
		/// A pass-through must copy; it cannot simply return. Reversibility makes this unavoidable
		/// anyway: nothing can reconstruct N bytes from none.
		/// </remarks>
		void Apply(ReadOnlySpan<byte> src, IBufferWriter<byte> dst);

		/// <summary>Load direction: undoes <see cref="Apply"/>, appending the result to dst.</summary>
		/// <remarks>Subject to the same write obligation as <see cref="Apply"/>.</remarks>
		void Reverse(ReadOnlySpan<byte> src, IBufferWriter<byte> dst);
	}
}
