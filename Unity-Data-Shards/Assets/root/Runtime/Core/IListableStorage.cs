using System.Collections.Generic;
using System.Threading;
#if PERSISTENCE_HAS_UNITASK
using IntTask = Cysharp.Threading.Tasks.UniTask<int>;
#else
using IntTask = System.Threading.Tasks.Task<int>;
#endif

namespace Saesentsessis.Persistence.Core
{
	/// <summary>
	/// Optional capability: an <see cref="IStorage"/> that can report which keys it holds.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Deliberately separate from <see cref="IStorage"/>. Folding enumeration into that interface
	/// would break every existing implementation, and not every medium can enumerate — PlayerPrefs
	/// exposes no key listing at all. Detect the capability instead:
	/// </para>
	/// <code>
	/// if (storage is IListableStorage listable)
	///     await listable.PopulateAsync(keys);
	/// </code>
	/// <para>
	/// Enumeration belongs on the storage rather than in a parallel hierarchy because the storage
	/// already holds what listing needs — a root directory and an extension, a key postfix, a
	/// signed-in player. A separate lister would have to be configured with those a second time, and
	/// could be paired with the wrong backend.
	/// </para>
	/// <para>
	/// <b>Decorators</b> such as <c>TransformStorage</c> implement this by forwarding, so
	/// <c>is IListableStorage</c> is necessary but not sufficient — the call still fails when what
	/// they wrap cannot enumerate.
	/// </para>
	/// </remarks>
	public interface IListableStorage
	{
		/// <summary>
		/// Appends one <see cref="StorageKeyInfo"/> per stored key, and returns how many were added.
		/// </summary>
		/// <param name="destination">
		/// Sink to append to. <b>Not cleared</b>, so several storages can accumulate into one list.
		/// Reusing a single pre-sized list across refreshes keeps the steady state free of growth
		/// allocations.
		/// </param>
		/// <param name="cancellation">
		/// Best-effort: a backend already mid-walk may finish it rather than abandon a partial result.
		/// </param>
		/// <remarks>
		/// Order is unspecified — sort if you need one. Keys come back in the form
		/// <see cref="IStorage"/> accepts, so any key from here can be handed straight to
		/// <see cref="IStorage.TryReadAsync"/>. They are never filesystem paths: exposing those would
		/// leak the root a file backend confines its keys beneath.
		/// </remarks>
		IntTask PopulateAsync(IList<StorageKeyInfo> destination, CancellationToken cancellation = default);
	}
}
