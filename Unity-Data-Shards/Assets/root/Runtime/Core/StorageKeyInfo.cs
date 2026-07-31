using System;

namespace Saesentsessis.Persistence.Core
{
	/// <summary>
	/// One key found by <see cref="IListableStorage"/>, with the metadata a backend can report
	/// without reading the value.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Everything here comes free from the enumeration itself — a directory walk already carries
	/// length and write time, and Cloud Save's file listing does too. That is what makes a slot
	/// browser's first pass cheap: it can size and sort a list without opening a single save.
	/// </para>
	/// <para>
	/// The <see cref="Key"/> reference is the only allocation in this struct, and one string per key
	/// is the floor for any string-keyed listing. Callers that enumerate repeatedly should reuse a
	/// single destination list rather than trying to shrink this.
	/// </para>
	/// </remarks>
	public readonly struct StorageKeyInfo
	{
		/// <summary>Storage key in the form <see cref="IStorage"/> accepts — never a filesystem path.</summary>
		public readonly string Key;

		/// <summary>Stored size in bytes, as the backend holds it — before any transform is reversed.</summary>
		public readonly long Size;

		/// <summary>
		/// Last write time in UTC ticks, or <c>0</c> where the backend has no such concept —
		/// PlayerPrefs, for one.
		/// </summary>
		/// <remarks>
		/// Raw ticks rather than a <see cref="DateTime"/>, so listing several hundred keys does not
		/// construct one per entry for a column that may never be shown.
		/// </remarks>
		public readonly long ModifiedUtcTicks;

		public StorageKeyInfo(string key, long size, long modifiedUtcTicks = 0)
		{
			Key = key;
			Size = size;
			ModifiedUtcTicks = modifiedUtcTicks;
		}

		/// <summary>True when the backend supplied a write time this can be read as a date.</summary>
		public bool HasModifiedTime => UtcTicks.IsValid(ModifiedUtcTicks);

		/// <summary>
		/// <see cref="ModifiedUtcTicks"/> as a <see cref="DateTime"/>, computed on demand.
		/// <see cref="DateTime.MinValue"/> when the backend reported nothing — or reported something
		/// no date could hold, which a custom backend is free to do.
		/// </summary>
		public DateTime ModifiedUtc => UtcTicks.ToDateTime(ModifiedUtcTicks);
	}
}
