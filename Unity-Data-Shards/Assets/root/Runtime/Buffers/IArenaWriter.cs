using System.Buffers;

namespace Saesentsessis.Persistence.Buffers
{
	/// <summary>
	/// What the save pipeline needs from an arena beyond <see cref="IBufferWriter{T}"/>:
	/// the committed length, so blob ranges can be computed as before/after deltas.
	/// Implemented by <see cref="NativeListBufferWriter"/> and <see cref="PooledArrayBufferWriter"/>.
	/// </summary>
	internal interface IArenaWriter : IBufferWriter<byte>
	{
		int WrittenLength { get; }
	}
}
