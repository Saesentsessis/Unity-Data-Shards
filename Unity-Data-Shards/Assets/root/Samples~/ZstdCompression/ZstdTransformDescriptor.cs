using System;
using Saesentsessis.Persistence.Core;
using Saesentsessis.Persistence.Storage.Transforms.Zstd;
using UnityEngine;

namespace Saesentsessis.Persistence.Configuration.Storage.Transforms
{
	[Serializable]
	public class ZstdTransformDescriptor : ITransformDescriptor
	{
		[SerializeField] private int compressionLevel = 3;
		
		public IStorageTransform Create() => new ZstdTransform(compressionLevel);
	}
}