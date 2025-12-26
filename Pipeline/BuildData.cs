using System;
using Nox.CCK.Mods;
using Nox.CCK.Utils;
using UnityEditor;

namespace Nox.GameBuilder.Pipeline {
	public class BuildData {
		public string                OutputPath;
		public Platform              Target;
		public string                BuildName;
		public BuildOptions          BuildOptions = BuildOptions.None;
		public IMod[]                Mods;
		public Action<float, string> ProgressCallback = (_, _) => { };
	}
}