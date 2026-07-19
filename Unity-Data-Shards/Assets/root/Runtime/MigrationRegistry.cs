using System;
using System.Collections.Generic;
using Persistence.Buffers;
using Persistence.Core;

namespace Persistence
{
	public sealed class MigrationRegistry
	{
		// Register() cannot detect cross-type cycles (A -> B -> A); this cap turns an
		// accidental cycle into an exception instead of an infinite loop at load time.
		private const int MaxChainLength = 64;

		private readonly Dictionary<SchemaState, IShardMigration> _migrations = new();

		public void Register(IShardMigration migration)
		{
			if (migration.FromTypeName == migration.ToType.FullName && migration.ToVersion <= migration.FromVersion)
				throw new ArgumentException($"Same-type migration ToVersion ({migration.ToVersion}) must be greater than FromVersion ({migration.FromVersion}).");

			var state = new SchemaState(migration.FromTypeName, migration.FromVersion);

			if (_migrations.TryAdd(state, migration) == false)
				throw new ArgumentException($"Migration already registered for {migration.FromTypeName} v{migration.FromVersion}. Branching paths are not supported.");
		}

		/// <summary>True if a migration chain starts at the given stored state.</summary>
		public bool HasMigration(string typeName, int version)
			=> _migrations.ContainsKey(new SchemaState(typeName, version));

		/// <summary>
		/// Runs the migration chain on raw blob bytes, ping-ponging between two pooled
		/// buffers (one live copy at a time). Returns the writer holding the final bytes —
		/// the caller deserializes from <c>WrittenSpan</c> as <paramref name="finalType"/>
		/// and MUST dispose the writer. Only call after <see cref="HasMigration"/> is true.
		/// </summary>
		public PooledArrayBufferWriter MigrateToLatest(ReadOnlySpan<byte> blob, string typeName, int storedVersion, out Type finalType)
		{
			PooledArrayBufferWriter front = null, back = null;
			var current = blob;
			finalType = null;
			var name = typeName;
			var version = storedVersion;
			var steps = 0;

			try
			{
				while (_migrations.TryGetValue(new SchemaState(name, version), out var migration))
				{
					if (++steps > MaxChainLength)
						throw new InvalidOperationException(
							$"Migration chain starting at {typeName} v{storedVersion} exceeded {MaxChainLength} steps — registered migrations likely form a cycle.");

					front ??= new PooledArrayBufferWriter(current.Length + 64);
					front.Clear();
					migration.Migrate(current, front);

					finalType = migration.ToType;
					name = finalType.FullName;
					version = migration.ToVersion;
					current = front.WrittenSpan;

					// The next step writes into the other buffer while reading this one.
					(front, back) = (back, front);
				}

				if (finalType == null)
					throw new InvalidOperationException($"No migration registered for {typeName} v{storedVersion}.");

				var targetVersion = ShardSchemaHelper.GetVersion(finalType);

				if (version < targetVersion)
					throw new InvalidOperationException($"Migration chain is broken. Reached {finalType.Name} v{version}, but schema requires v{targetVersion}.");

				if (version > targetVersion)
					throw new InvalidOperationException($"Data version ({version}) exceeds schema version ({targetVersion}) for {finalType.Name}.");

				// After the final swap the result lives in 'back'; 'front' is scratch.
				front?.Dispose();
				return back;
			}
			catch
			{
				front?.Dispose();
				back?.Dispose();
				throw;
			}
		}
	}
}
