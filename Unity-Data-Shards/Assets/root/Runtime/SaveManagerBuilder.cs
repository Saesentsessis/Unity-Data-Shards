using System;
using Saesentsessis.Persistence.Core;

namespace Saesentsessis.Persistence
{
	/// <summary>
	/// Fluent builder for <see cref="SaveManager"/>. Configures the serializer, the layout
	/// (either <see cref="ISaveLayout"/> or <see cref="IManagedSaveLayout"/>), and migrations
	/// supplied as a ready <see cref="MigrationRegistry"/> or a <see cref="MigrationRegistryBuilder"/>.
	/// Pure sugar over the <see cref="SaveManager"/> constructors — the resulting manager still
	/// binds the serializer into the registry itself.
	/// </summary>
	public sealed class SaveManagerBuilder
	{
		private ISerializer _serializer;
		private ISaveLayout _layout;
		private IManagedSaveLayout _managedLayout;
		private MigrationRegistry _registry;
		private MigrationRegistryBuilder _registryBuilder;

		public SaveManagerBuilder WithSerializer(ISerializer serializer)
		{
			_serializer = serializer;
			return this;
		}

		public SaveManagerBuilder WithLayout(ISaveLayout layout)
		{
			_layout = layout;
			_managedLayout = null;
			return this;
		}

		public SaveManagerBuilder WithLayout(IManagedSaveLayout layout)
		{
			_managedLayout = layout;
			_layout = null;
			return this;
		}

		public SaveManagerBuilder WithMigrations(MigrationRegistry registry)
		{
			_registry = registry;
			_registryBuilder = null;
			return this;
		}

		public SaveManagerBuilder WithMigrations(MigrationRegistryBuilder registryBuilder)
		{
			_registryBuilder = registryBuilder;
			_registry = null;
			return this;
		}

		/// <summary>
		/// Validates configuration, resolves the migration registry (building it if a
		/// <see cref="MigrationRegistryBuilder"/> was supplied), and constructs the
		/// <see cref="SaveManager"/> against the configured layout.
		/// </summary>
		public SaveManager Build()
		{
			if (_serializer == null)
				throw new InvalidOperationException("A serializer is required; call WithSerializer before Build.");

			if (_layout == null && _managedLayout == null)
				throw new InvalidOperationException("A layout is required; call WithLayout before Build.");

			if (_layout != null && _managedLayout != null)
				throw new InvalidOperationException("Configure exactly one layout kind (ISaveLayout or IManagedSaveLayout), not both.");

			var migrations = _registryBuilder?.Build() ?? _registry;

			return _layout != null
				? new SaveManager(_serializer, _layout, migrations)
				: new SaveManager(_serializer, _managedLayout, migrations);
		}
	}
}
