using System;
using Saesentsessis.Persistence.Core;
using Saesentsessis.Persistence.Storage;
using UnityEngine;

namespace Saesentsessis.Persistence.Configuration.Storage
{
	/// <summary>
	/// Builds a <see cref="PlayerPrefsStorage"/> — the small-payload backend.
	/// </summary>
	/// <remarks>
	/// Unity's guidance is 2 KB per value, and base64 costs a further third, so this suits settings
	/// and slot indexes rather than save data. tvOS and iOS enforce real ceilings; everywhere else
	/// the limit is advisory. Prefer <see cref="FileStorageDescriptor"/> for anything substantial.
	/// </remarks>
	[Serializable]
	public sealed class PlayerPrefsStorageDescriptor : IStorageDescriptor
	{
		[Tooltip("Appended to every key, keeping these entries distinguishable from other PlayerPrefs.")]
		[SerializeField] private string postfix = "save";

		[SerializeField] private PlayerPrefsStorage.Options options = PlayerPrefsStorage.DefaultOptions;

		/// <inheritdoc />
		public IStorage Create() => new PlayerPrefsStorage(postfix, options);
	}
}