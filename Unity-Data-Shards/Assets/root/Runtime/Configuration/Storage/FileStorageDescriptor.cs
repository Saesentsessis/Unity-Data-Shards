using System;
using Saesentsessis.Persistence.Attributes;
using Saesentsessis.Persistence.Core;
using Saesentsessis.Persistence.Storage;
using UnityEngine;

namespace Saesentsessis.Persistence.Configuration.Storage
{
	/// <summary>
	/// Builds a <see cref="FileStorage"/> — the default backend, with no practical size ceiling.
	/// </summary>
	/// <remarks>
	/// Both fields feed straight into the constructor, which normalizes the root and confines every
	/// key beneath it. A root left empty resolves to <c>Application.persistentDataPath</c>, which is
	/// what a shipped game almost always wants; a relative root resolves against the process working
	/// directory, and in the editor that is the Unity installation, not the project.
	/// </remarks>
	[Serializable]
	public sealed class FileStorageDescriptor : IStorageDescriptor
	{
		[Tooltip("Empty - Application.persistentDataPath")]
		[SerializeField, SystemPath(isDirectory: true)] private string rootDirectory;

		[Tooltip("Appended to every key. Saves land at <root>/<key>.<extension>.")]
		[SerializeField] private string fileExtension = "save";

		/// <inheritdoc />
		public IStorage Create() => new FileStorage(rootDirectory, fileExtension);
	}
}