#if UNITY_STANDALONE_LINUX || UNITY_EDITOR_LINUX
using System;
using System.Buffers;
using System.IO;
using UnityEngine;

namespace Saesentsessis.Persistence.Utils
{
	/// <summary>
	/// Enumerates PlayerPrefs keys from the Unity preferences file on Linux,
	/// <c>~/.config/unity3d/&lt;company&gt;/&lt;product&gt;/prefs</c>.
	/// </summary>
	/// <remarks>
	/// Path resolution only — <see cref="UnityPrefsXmlReader"/> does the parsing, and lives outside
	/// the platform gate so it can be tested somewhere other than Linux.
	/// </remarks>
	internal sealed class LinuxPlayerPrefsReader : IPlayerPrefsReader
	{
		private readonly string _path;

		private PrefsKeyBuffer _buffer;

		public LinuxPlayerPrefsReader()
		{
			// XDG_CONFIG_HOME is the documented override for ~/.config. Unity uses the default, but
			// a user who relocated their config directory relocated this with it.
			var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

			if (string.IsNullOrEmpty(config))
			{
				var home = PrefsKeyBuffer.HomeDirectory();
				config = string.IsNullOrEmpty(home) ? null : Path.Combine(home, ".config");
			}

			_path = config == null
				? null
				: Path.Combine(config, "unity3d", Application.companyName, Application.productName, "prefs");
		}

		public ReadOnlySpan<string> EnumerateKeys(string postfix)
		{
			_buffer.Reset(_buffer.Storage);

			if (_path == null)
				return ReadOnlySpan<string>.Empty;

			var bytes = PrefsKeyBuffer.TryReadFile(_path, out var length);

			if (bytes == null)
				return ReadOnlySpan<string>.Empty;

			try
			{
				UnityPrefsXmlReader.Read(new ReadOnlySpan<byte>(bytes, 0, length), postfix, ref _buffer);
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(bytes);
			}

			return _buffer.Keys;
		}

		public void Dispose()
		{
			_buffer.Reset(Array.Empty<string>());
		}
	}
}
#endif
