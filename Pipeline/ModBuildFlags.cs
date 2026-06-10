using System;

namespace Nox.GameBuilder.Pipeline {
	[Flags]
	public enum ModBuildFlags {
		None                  = 0,
		/// <summary>
		/// Delete and recreate the output directory before building if it is not empty.
		/// Without this flag the directory is kept as-is.
		/// </summary>
		AutoConfirmClearOutput = 1 << 0,
	}
}
