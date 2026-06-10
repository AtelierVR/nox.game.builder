using System;
using Nox.CCK.Utils;
using UnityEditor;

namespace Nox.GameBuilder.Pipeline {
	public class ModBuildData {
		public string[]              ModIds;
		public Platform              Target;
		public string                OutputPath;
		public BuildOptions          BuildOptions = BuildOptions.None;
		public ModBuildFlags         Flags        = ModBuildFlags.None;
		public Action<float, string> ProgressCallback = (_, _) => { };
	}
}
