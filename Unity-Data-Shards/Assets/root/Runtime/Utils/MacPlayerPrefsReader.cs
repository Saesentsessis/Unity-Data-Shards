#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
using System;
using System.Buffers;
using System.IO;
using UnityEngine;

namespace Saesentsessis.Persistence.Utils
{
	/// <summary>
	/// Enumerates PlayerPrefs keys from the <c>NSUserDefaults</c> property list Unity writes on
	/// macOS, under <c>~/Library/Preferences</c>.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Path resolution only — <see cref="PlistKeyReader"/> does the parsing, and lives outside the
	/// platform gate so it can be tested somewhere other than a Mac.
	/// </para>
	/// <para>
	/// <b>The file name is probed rather than derived.</b> Sources disagree between
	/// <c>unity.&lt;company&gt;.&lt;product&gt;.plist</c> and a bundle-identifier name, and the
	/// answer has moved across Unity versions. A wrong guess fails as "no saves exist", which is
	/// indistinguishable from the truth, so every known shape is tried and the first that exists
	/// wins.
	/// </para>
	/// </remarks>
	internal sealed class MacPlayerPrefsReader : IPlayerPrefsReader
	{
		private readonly string[] _candidates;

		private PrefsKeyBuffer _buffer;

		public MacPlayerPrefsReader()
		{
			var home = PrefsKeyBuffer.HomeDirectory();

			if (string.IsNullOrEmpty(home))
			{
				_candidates = Array.Empty<string>();
				return;
			}

			var preferences = Path.Combine(home, "Library", "Preferences");

			_candidates = new[]
			{
				Path.Combine(preferences, $"unity.{Application.companyName}.{Application.productName}.plist"),
				Path.Combine(preferences, $"com.{Application.companyName}.{Application.productName}.plist"),
				Path.Combine(preferences, $"{Application.identifier}.plist")
			};
		}

		public ReadOnlySpan<string> EnumerateKeys(string postfix)
		{
			_buffer.Reset(_buffer.Storage);

			for (var i = 0; i < _candidates.Length; i++)
			{
				var bytes = PrefsKeyBuffer.TryReadFile(_candidates[i], out var length);

				if (bytes == null)
					continue;

				try
				{
					PlistKeyReader.Read(new ReadOnlySpan<byte>(bytes, 0, length), postfix, ref _buffer);
				}
				finally
				{
					ArrayPool<byte>.Shared.Return(bytes);
				}

				return _buffer.Keys;
			}

			return ReadOnlySpan<string>.Empty;
		}

		public void Dispose()
		{
			_buffer.Reset(Array.Empty<string>());
		}
	}
}
#endif
