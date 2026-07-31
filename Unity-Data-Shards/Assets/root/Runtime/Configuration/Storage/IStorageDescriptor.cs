using Saesentsessis.Persistence.Core;

namespace Saesentsessis.Persistence.Configuration.Storage
{
	/// <summary>
	/// Serializable recipe for an <see cref="IStorage"/> — the inspector-editable form of a storage
	/// backend.
	/// </summary>
	/// <remarks>
	/// <para>
	/// A storage is a live resource: it holds caches and gates, it is <see cref="System.IDisposable"/>,
	/// and its constructor may touch Unity APIs that only answer on the main thread. None of that
	/// belongs in serialized data, so what gets serialized is this — plain fields that describe how
	/// to build one. Implementations must therefore be <c>[Serializable]</c> classes with a
	/// parameterless constructor, and are assigned through <c>[SerializeReference]</c>.
	/// </para>
	/// <para>
	/// <b>Ownership:</b> <see cref="Create"/> hands the caller a new instance every call, and the
	/// caller disposes it. A descriptor holds no reference to what it built.
	/// </para>
	/// </remarks>
	public interface IStorageDescriptor
	{
		/// <summary>Builds a storage from the configured values. Never returns the same instance twice.</summary>
		IStorage Create();
	}
}