using System;

namespace Saesentsessis.Persistence.Core
{
	/// <summary>
	/// Thrown when persisted save data fails integrity validation: checksum mismatch,
	/// truncated stream, or structurally impossible values (counts/lengths exceeding
	/// the buffer). Derives from <see cref="InvalidOperationException"/> so existing
	/// catch-alls keep working.
	/// </summary>
	public class SaveCorruptedException : InvalidOperationException
	{
		public SaveCorruptedException(string message) : base(message) { }
	}
}
