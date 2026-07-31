using UnityEngine;

namespace Saesentsessis.Persistence.Attributes
{
	/// <summary>
	/// Draws a string field as a filesystem path with a browse button beside it.
	/// </summary>
	/// <remarks>
	/// The path is still editable as text — the picker is a convenience, not the only way in, so a
	/// path that does not exist yet (or lives on another machine) can be typed directly.
	/// </remarks>
	internal class SystemPathAttribute : PropertyAttribute
	{
		/// <summary>Title of the browse dialog. Kept per-field so the attribute stays general.</summary>
		public readonly string Title;
		
		/// <summary>
		/// Means nothing for a runtime. The only thing it changes is how would system's
		/// native file explorer be opened - as a file or a folder picker.
		/// </summary>
		public readonly bool IsDirectory;

		/// <param name="title">Shown in the file browser's title bar.</param>
		/// <param name="isDirectory">Should a convenience button open file or folder picker?</param>
		public SystemPathAttribute(string title = "", bool isDirectory = false)
		{
			Title = title ?? (isDirectory ? "Select directory" : "Select file");
			IsDirectory = isDirectory;
		}
	}
}