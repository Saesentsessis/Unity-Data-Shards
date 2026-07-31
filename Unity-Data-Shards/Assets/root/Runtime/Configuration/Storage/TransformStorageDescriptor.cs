using System;
using System.Linq;
using Saesentsessis.Persistence.Attributes;
using Saesentsessis.Persistence.Core;
using Saesentsessis.Persistence.Storage;
using UnityEngine;

namespace Saesentsessis.Persistence.Configuration.Storage
{
	/// <summary>
	/// Wraps another storage in a <see cref="TransformStorage"/> chain — compression, encryption, or
	/// anything else reversible.
	/// </summary>
	/// <remarks>
	/// <para>
	/// This is the composing descriptor: <see cref="inner"/> is itself an
	/// <see cref="IStorageDescriptor"/>, so chains nest to any depth in the inspector.
	/// </para>
	/// <para>
	/// <b>Order is the wire order.</b> Transforms apply top to bottom on write and reverse bottom to
	/// top on read, so compression belongs above encryption — ciphertext does not compress.
	/// </para>
	/// <para>
	/// The transforms built here belong to the returned storage and are disposed with it, so
	/// releasing the storage releases the whole chain.
	/// </para>
	/// </remarks>
	[Serializable]
	public sealed class TransformStorageDescriptor : IStorageDescriptor
	{
		[Tooltip("Storage the chain wraps. May itself be another TransformStorage.")]
		[SerializeReference, SubclassPicker] private IStorageDescriptor inner;

		[Tooltip("Applied in order on write, reversed on read. Compress before encrypting.")]
		[SerializeReference, SubclassPicker] private ITransformDescriptor[] transforms;

		/// <inheritdoc />
		/// <remarks>
		/// Every call builds a fresh chain, which is what the ownership rule requires: a transform
		/// belongs to exactly one storage, so two storages must never be handed the same instance.
		/// Disposing the returned storage releases the whole chain.
		/// </remarks>
		public IStorage Create()
		{
			if (inner == null)
				throw new InvalidOperationException(
					$"[{nameof(TransformStorageDescriptor)}] No inner storage configured.");

			var built = transforms == null
				? Array.Empty<IStorageTransform>()
				: transforms.Select(static transform => transform.Create()).ToArray();

			return new TransformStorage(inner.Create(), built);
		}
	}
}