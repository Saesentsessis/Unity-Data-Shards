using System;
using Saesentsessis.Persistence.Core;
using Saesentsessis.Persistence.Storage.Transforms;
using UnityEngine;
using CompressionLevel = System.IO.Compression.CompressionLevel;

namespace Saesentsessis.Persistence.Configuration.Storage.Transforms
{
	[Serializable]
	public sealed class DeflateTransformDescriptor : ITransformDescriptor
	{
		[SerializeField] private CompressionLevel compressionLevel;
		
		public IStorageTransform Create()
		{
			return new DeflateTransform(compressionLevel);
		}
	}
}