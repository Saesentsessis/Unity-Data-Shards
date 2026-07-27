using System;

namespace Saesentsessis.Persistence.Storage
{
	public class InvalidPathException : Exception
	{
		public InvalidPathException(string message) : base(message) { }
	}
}