using System;
using System.Buffers;

namespace Saesentsessis.Persistence.Core
{
	/// <summary>
	/// Beginner-friendly migration tier: instead of reshaping raw serialized bytes, an author
	/// deserializes the old shape, transforms it in plain C#, and lets the pipeline reserialize
	/// the result. This is an adapter over <see cref="IShardMigration"/> — the sealed
	/// <see cref="Migrate"/> performs <c>Deserialize → Convert → Serialize</c> using the active
	/// <see cref="ISerializer"/>, so the migration chain still sees a pure byte→byte step and
	/// <c>MigrationRegistry</c> stores it exactly like any other migration.
	/// <para>
	/// <typeparamref name="TOld"/> is the stored shape and may be a plain versioned snapshot
	/// POCO kept around only for migration; it must remain deserializable by the configured
	/// serializer. <typeparamref name="TNew"/> is the current shard type.
	/// </para>
	/// </summary>
	/// <typeparam name="TOld">The shape the stored blob deserializes into.</typeparam>
	/// <typeparam name="TNew">The shard type this step produces.</typeparam>
	public abstract class TypedShardMigration<TOld, TNew> : IShardMigration, ISerializerAware
		where TNew : class, IDataShard
	{
		private ISerializer _serializer;

		/// <param name="fromVersion">The schema version this migration accepts as input.</param>
		/// <param name="toVersion">The schema version produced after conversion.</param>
		protected TypedShardMigration(int fromVersion, int toVersion)
		{
			FromVersion = fromVersion;
			ToVersion = toVersion;
		}

		/// <summary>
		/// The stored (namespace-qualified) type name this migration accepts. Defaults to
		/// <typeparamref name="TOld"/>'s full name; override when the stored name differs from
		/// the snapshot class name (e.g. the original type was renamed).
		/// </summary>
		public virtual string FromTypeName => typeof(TOld).FullName;

		public int FromVersion { get; }
		public Type ToType => typeof(TNew);
		public int ToVersion { get; }

		void ISerializerAware.BindSerializer(ISerializer serializer) => _serializer = serializer;

		/// <summary>Transform the deserialized old shape into the current shard. Pure and stateless.</summary>
		protected abstract TNew Convert(TOld old);

		public void Migrate(ReadOnlySpan<byte> src, IBufferWriter<byte> dst)
		{
			if (_serializer == null)
				throw new InvalidOperationException(
					$"{GetType().Name} requires a serializer. Register it in a MigrationRegistry passed to a SaveManager (or built via SaveManagerBuilder) so the serializer can be bound.");

			var old = (TOld)_serializer.Deserialize(src, typeof(TOld));
			_serializer.Serialize(Convert(old), typeof(TNew), dst);
		}
	}
}
