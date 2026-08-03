using MessagePack;
using MessagePack.Formatters;
using MessagePack.Resolvers;
using Saesentsessis.Persistence.Core;

namespace Saesentsessis.Persistence.Serialization.MessagePack
{
	public class MyApplicationResolver : IFormatterResolver
	{
		public static readonly IFormatterResolver Instance = new MyApplicationResolver();

		private static readonly IFormatterResolver[] Resolvers =
		{
			// Add custom resolvers here
			StandardResolver.Instance
		};

		private MyApplicationResolver() { }

		public IMessagePackFormatter<T> GetFormatter<T>()
			=> Cache<T>.Formatter;

		private static class Cache<T>
		{
			public static IMessagePackFormatter<T> Formatter;

			static Cache()
			{
				if (typeof(T) == typeof(SerializableGuid))
				{
					Formatter = new SerializableGuidMessagePackFormatter() as IMessagePackFormatter<T>;
					return;
				}

				foreach (var resolver in Resolvers)
				{
					var formatter = resolver.GetFormatter<T>();

					if (formatter == null)
						continue;

					Formatter = formatter;
					break;
				}
			}
		}
	}
}