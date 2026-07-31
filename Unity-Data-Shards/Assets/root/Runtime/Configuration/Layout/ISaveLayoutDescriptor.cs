using Saesentsessis.Persistence.Core;

namespace Saesentsessis.Persistence.Configuration.Layout
{
	/// <summary>
	/// Serializable recipe for an <see cref="ISaveLayout"/>, the layout counterpart of
	/// <see cref="Storage.IStorageDescriptor"/>.
	/// </summary>
	/// <remarks>
	/// <para>
	/// A layout is built <i>over</i> a storage rather than beside it, so <see cref="Create"/> takes
	/// one. That also settles ownership: both shipped layouts dispose the storage they wrap, so
	/// disposing the returned layout releases the whole chain — do not dispose the storage as well.
	/// </para>
	/// <para>
	/// Implementations must be <c>[Serializable]</c> classes with a parameterless constructor, and
	/// are assigned through <c>[SerializeReference]</c>.
	/// </para>
	/// </remarks>
	public interface ISaveLayoutDescriptor
	{
		/// <summary>Builds a layout over <paramref name="storage"/>. The caller owns the result.</summary>
		ISaveLayout Create(IStorage storage);
	}
}
