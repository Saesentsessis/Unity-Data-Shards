using Saesentsessis.Persistence.Core;

namespace Saesentsessis.Persistence.Configuration.Storage
{
	/// <summary>
	/// Serializable recipe for an <see cref="IStorageTransform"/>, the transform counterpart of
	/// <see cref="IStorageDescriptor"/>.
	/// </summary>
	/// <remarks>
	/// Each call must return a <b>fresh</b> instance. A transform belongs to exactly one
	/// <c>TransformStorage</c>, which disposes it: transforms carry per-operation scratch state, so
	/// handing one instance to two chains would let them interleave through it. Returning a cached
	/// instance would break both halves of that — the sharing rule and the disposal rule.
	/// </remarks>
	public interface ITransformDescriptor
	{
		/// <summary>Builds a transform from the configured values. The caller owns and disposes it.</summary>
		IStorageTransform Create();
	}
}