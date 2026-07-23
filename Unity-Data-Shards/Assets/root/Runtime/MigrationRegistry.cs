using System;
using System.Collections.Generic;
using Saesentsessis.Persistence.Buffers;
using Saesentsessis.Persistence.Core;

namespace Saesentsessis.Persistence
{
	public sealed class MigrationRegistry
	{
		// Register() cannot detect cross-type cycles (A -> B -> A); this cap turns an
		// accidental cycle into an exception instead of an infinite loop at load time.
		private const int MaxChainLength = 64;

		private readonly Dictionary<SchemaState, IShardMigration> _migrations = new();
		private ISerializer _serializer;

		public MigrationRegistry() { }
		
		public MigrationRegistry(IReadOnlyList<IShardMigration> migrations)
		{
			_migrations.EnsureCapacity(migrations.Count);

			for (var i = migrations.Count - 1; i >= 0; i--)
			{
				var migration = migrations[i];
				
				if (migration.FromTypeName == migration.ToType.FullName && migration.ToVersion <= migration.FromVersion)
					throw new ArgumentException($"Same-type migration ToVersion ({migration.ToVersion}) must be greater than FromVersion ({migration.FromVersion}).");

				var state = new SchemaState(migration.FromTypeName, migration.FromVersion);

				if (_migrations.TryAdd(state, migration) == false)
					throw new ArgumentException($"Migration already registered for {migration.FromTypeName} v{migration.FromVersion}. Branching paths are not supported.");
			}
		}

		public void Register(IShardMigration migration)
		{
			if (migration.FromTypeName == migration.ToType.FullName && migration.ToVersion <= migration.FromVersion)
				throw new ArgumentException($"Same-type migration ToVersion ({migration.ToVersion}) must be greater than FromVersion ({migration.FromVersion}).");

			var state = new SchemaState(migration.FromTypeName, migration.FromVersion);

			if (_migrations.TryAdd(state, migration) == false)
				throw new ArgumentException($"Migration already registered for {migration.FromTypeName} v{migration.FromVersion}. Branching paths are not supported.");

			// If the serializer is already known (registry reached a SaveManager before this
			// late registration), bind it now; otherwise BindSerializer will pick it up later.
			if (_serializer != null && migration is ISerializerAware aware)
				aware.BindSerializer(_serializer);
		}

		/// <summary>
		/// Supplies the active serializer to every registered <see cref="ISerializerAware"/>
		/// migration (e.g. <see cref="TypedShardMigration{TOld,TNew}"/>). Called by
		/// <see cref="SaveManager"/> at construction; made order-independent by also binding
		/// from <see cref="Register"/> once the serializer is known.
		/// </summary>
		internal void BindSerializer(ISerializer serializer)
		{
			_serializer = serializer;

			foreach (var migration in _migrations.Values)
			{
				if (migration is ISerializerAware aware)
					aware.BindSerializer(serializer);
			}
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
			PooledArrayBufferWriter frontBuffer = null, backBuffer = null;
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

					frontBuffer ??= new PooledArrayBufferWriter(current.Length + 64);
					frontBuffer.Clear();
					migration.Migrate(current, frontBuffer);

					finalType = migration.ToType;
					name = finalType.FullName;
					version = migration.ToVersion;
					current = frontBuffer.WrittenSpan;

					// The next step writes into the other buffer while reading this one.
					(frontBuffer, backBuffer) = (backBuffer, frontBuffer);
				}

				if (finalType == null)
					throw new InvalidOperationException($"No migration registered for {typeName} v{storedVersion}.");

				var targetVersion = ShardSchemaHelper.GetVersion(finalType);

				if (version < targetVersion)
					throw new InvalidOperationException($"Migration chain is broken. Reached {finalType.Name} v{version}, but schema requires v{targetVersion}.");

				if (version > targetVersion)
					throw new InvalidOperationException($"Data version ({version}) exceeds schema version ({targetVersion}) for {finalType.Name}.");

				// After the final swap the result lives in 'back'; 'front' is scratch.
				frontBuffer?.Dispose();
				return backBuffer;
			}
			catch
			{
				frontBuffer?.Dispose();
				backBuffer?.Dispose();
				throw;
			}
		}
	}
}
