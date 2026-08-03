using System;
using K4os.Compression.LZ4;
using Saesentsessis.Persistence.Core;
using Saesentsessis.Persistence.Storage.Transforms.LZ4;
using UnityEngine;

namespace Saesentsessis.Persistence.Configuration.Storage.Transforms
{
	[Serializable]
	public sealed class LZ4TransformDescriptor : ITransformDescriptor
	{
		[SerializeField] private LZ4Level compressionLevel = LZ4Level.L00_FAST;
		
		public IStorageTransform Create() => new LZ4Transform(compressionLevel);
	}
}