using System;
using System.IO;
using Saesentsessis.Persistence.Attributes;
using Saesentsessis.Persistence.Core;
using Saesentsessis.Persistence.Storage.Transforms;
using Unity.Collections;
using UnityEngine;

namespace Saesentsessis.Persistence.Configuration.Storage.Transforms
{
	/// <summary>
	/// Builds an <see cref="AesCbcHmacTransform"/> from key material held in a file on disk.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>The key is a path, never a serialized value.</b> A key typed into an inspector field is
	/// written into the asset — and into whatever the asset is committed to. Pointing at a file
	/// keeps the material out of the project entirely, so the configuration can be shared while the
	/// key is not. The file is read only when <see cref="Create"/> is called, so it never needs to
	/// exist on the machine doing the editing.
	/// </para>
	/// <para>
	/// It still ships inside the build wherever the game reads it from, so this remains
	/// obfuscation rather than secrecy — see the remarks on <see cref="AesCbcHmacTransform"/>. What
	/// it buys is real tamper detection, which the envelope's unkeyed checksum cannot provide.
	/// </para>
	/// <para>
	/// The transform returned is <see cref="IDisposable"/> and this descriptor does not own it;
	/// <c>TransformStorage</c> will not dispose it either. It is your responsibility.
	/// </para>
	/// </remarks>
	[Serializable]
	public sealed class AesCbcHmacTransformDescriptor : ITransformDescriptor
	{
		/// <summary>
		/// Anything longer is far past a key and is almost certainly the wrong file — refusing early
		/// gives a better error than letting a multi-megabyte read succeed.
		/// </summary>
		private const int MaxKeyBytes = ushort.MaxValue;

		[Tooltip("File holding the raw key bytes. At least 16 bytes; read only when the transform is built.")]
		[SerializeField, SystemPath("Select file with encryption key")] private string keyPath;

		[SerializeField] private AesCbcHmacTransform.Options options = AesCbcHmacTransform.DefaultOptions;

		/// <inheritdoc />
		public IStorageTransform Create()
		{
			if (string.IsNullOrEmpty(keyPath))
				throw new InvalidOperationException(
					$"[{nameof(AesCbcHmacTransformDescriptor)}] No key file configured.");

			using var stream = File.OpenRead(keyPath);

			var length = stream.Length;

			if (length > MaxKeyBytes)
				throw new ArgumentException("File snapshot is too large for an encryption key.", nameof(keyPath));

			// Native rather than managed: key material on the GC heap can be copied around by a
			// compacting collector, leaving unreachable duplicates that nothing can wipe. An
			// unmanaged block stays where it is put and is cleared below.
			using var bytes = new NativeArray<byte>((int)length, Allocator.Temp, NativeArrayOptions.UninitializedMemory);

			var expectedLength = (int)length;
			var readLength = stream.Read(bytes.AsSpan());

			// Stream.Read may return a short count on any backing store; loop until it stops
			// producing rather than assuming one call drains the file.
			while (readLength < expectedLength)
			{
				var delta = stream.Read(bytes.AsSpan()[readLength..]);

				if (delta == 0)
					break;

				readLength += delta;
			}

			try
			{
				// The constructor derives its subkeys and copies what it keeps, so nothing here is
				// needed once it returns.
				return new AesCbcHmacTransform(bytes.AsSpan()[..readLength], options);
			}
			finally
			{
				// Runs before the using disposes the array, so the block is zeroed rather than
				// merely freed. Matches the wiping AesCbcHmacTransform does for its own copies.
				bytes.AsSpan().Clear();
			}
		}
	}
}
