using System;
using Saesentsessis.Persistence.Core;
using Saesentsessis.Persistence.Storage.Transforms;
using UnityEngine;

namespace Saesentsessis.Persistence.Configuration.Storage.Transforms
{
	/// <summary>
	/// Builds an <see cref="XorTransform"/> — byte masking with a repeating pattern.
	/// </summary>
	/// <remarks>
	/// <b>Obfuscation, not encryption.</b> An XOR mask is recovered instantly from known plaintext,
	/// and a save file is full of it — the <c>SHRD</c> magic sits at a fixed offset in every
	/// envelope. It stops a player editing a save in a text editor and nothing more; reach for
	/// <see cref="AesCbcHmacTransformDescriptor"/> when tamper <i>detection</i> matters.
	/// An empty or all-zero pattern is a no-op mask.
	/// </remarks>
	[Serializable]
	public sealed class XorTransformDescriptor : ITransformDescriptor
	{
		[Tooltip("Repeated across the payload. Empty or all-zero masks nothing.")]
		[SerializeField] private byte[] pattern;

		/// <inheritdoc />
		public IStorageTransform Create() => new XorTransform(pattern);
	}
}