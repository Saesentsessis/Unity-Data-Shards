#if PERSISTENCE_HAS_CLOUDSAVE
using System;
using Saesentsessis.Persistence.Core;
using Saesentsessis.Persistence.Storage.CloudSave;
using UnityEngine;

namespace Saesentsessis.Persistence.Configuration.Storage
{
	[Serializable]
	public sealed class CloudSaveStorageDescriptor : IStorageDescriptor
	{
		[SerializeField] private char reservedCharacter = '.';
		
		public IStorage Create() => new CloudSaveStorage(reservedCharacter);
	}
}
#endif