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
		/// <summary>Release version string (e.g. "26.18.1-indev-9"), passed via -noxReleaseVersion.</summary>
		public string                Version;
		/// <summary>Release channel (e.g. "indev-9"), passed via -noxReleaseChannel.</summary>
		public string                Channel;
	}
}