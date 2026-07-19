using System;
using System.Buffers;

namespace Persistence.Core
{
	/// <summary>
	/// A reversible byte-level transform (compression, encryption, ...) applied to the
	/// fully packed save buffer at the storage boundary — see <c>TransformStorage</c>.
	/// Implementations must be stateless and pure; <see cref="Reverse"/> must exactly
	/// undo <see cref="Apply"/>.
	/// </summary>
	public interface ISaveTransform
	{
		/// <summary>Save direction: transforms src and appends the result to dst.</summary>
		void Apply(ReadOnlySpan<byte> src, IBufferWriter<byte> dst);

		/// <summary>Load direction: undoes <see cref="Apply"/>, appending the result to dst.</summary>
		void Reverse(ReadOnlySpan<byte> src, IBufferWriter<byte> dst);
	}
}
