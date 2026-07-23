using System.Collections.Generic;
using Saesentsessis.Persistence.Core;

namespace Saesentsessis.Persistence
{
	/// <summary>
	/// Fluent collector for migrations. Gathers raw <see cref="IShardMigration"/> and
	/// <see cref="TypedShardMigration{TOld,TNew}"/> steps (a typed migration is an
	/// <see cref="IShardMigration"/>, so one list holds both) and materializes a
	/// <see cref="MigrationRegistry"/> on <see cref="Build"/>. Serializer binding is not the
	/// builder's concern — it happens when the built registry reaches a <see cref="SaveManager"/>.
	/// </summary>
	public sealed class MigrationRegistryBuilder
	{
		private readonly List<IShardMigration> _migrations = new();

		/// <summary>Adds a migration (raw or typed). Returns this builder for chaining.</summary>
		public MigrationRegistryBuilder Add(IShardMigration migration)
		{
			_migrations.Add(migration);
			return this;
		}

		/// <summary>
		/// Creates a <see cref="MigrationRegistry"/> and registers every collected migration,
		/// preserving <see cref="MigrationRegistry.Register"/>'s duplicate/version validation.
		/// </summary>
		public MigrationRegistry Build()
		{
			return new MigrationRegistry(_migrations);
		}
	}
}
