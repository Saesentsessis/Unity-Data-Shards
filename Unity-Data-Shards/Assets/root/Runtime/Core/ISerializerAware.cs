namespace Saesentsessis.Persistence.Core
{
	/// <summary>
	/// Implemented by migrations that need the pipeline's active <see cref="ISerializer"/>
	/// (e.g. <see cref="TypedShardMigration{TOld,TNew}"/>, which deserializes and reserializes
	/// around a typed conversion). The <see cref="SaveManager"/> binds the serializer through
	/// the <c>MigrationRegistry</c> at construction time; raw <see cref="IShardMigration"/>
	/// implementations that work directly on bytes simply do not implement this.
	/// </summary>
	public interface ISerializerAware
	{
		/// <summary>Supplies the serializer the pipeline will use for this migration.</summary>
		void BindSerializer(ISerializer serializer);
	}
}
