using System;
using Cysharp.Threading.Tasks;
using Nox.CCK.Utils;
using Nox.ModLoader;
using UnityEditor;
using UnityEngine;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.GameBuilder.Pipeline {
	/// <summary>
	/// Headless/CI entry point. Invoke via Unity's -executeMethod flag:
	///   -executeMethod Nox.GameBuilder.Pipeline.ExternalBuilder.Build
	///
	/// game.ci unity-builder automatically passes:
	///   -buildTarget      &lt;StandaloneWindows64 | StandaloneLinux64 | ...&gt;
	///   -customBuildPath  &lt;output directory&gt;
	///   -customBuildName  &lt;executable name&gt;
	/// </summary>
	public static class ExternalBuilder {
		/// <summary>
		/// Synchronous entry point called by Unity (-executeMethod).
		/// Kicks off the async build and lets the Unity event loop process it.
		/// Do NOT pass -quit to Unity; this method calls EditorApplication.Exit itself.
		/// </summary>
		public static void Build() {
			RunBuildAsync().Forget();
		}

		private static async UniTaskVoid RunBuildAsync() {
			try {
				var args      = Environment.GetCommandLineArgs();
				var output    = GetArg(args, "-customBuildPath") ?? "build";
				var buildName = GetArg(args, "-customBuildName") ?? Application.productName;
				var platform  = PlatformExtensions.CurrentPlatform;

				Logger.Log($"[ExternalBuilder] platform={platform.GetPlatformName()}, output={output}, name={buildName}");

				// Discover and load all mods (kernel mods will be filtered inside Builder)
				await ModManager.LoadMods();

				var data = new BuildData {
					OutputPath       = output,
					BuildName        = buildName,
					Target           = platform,
					Mods             = ModManager.GetMods(),
					ProgressCallback = (p, m) => Logger.Log($"[ExternalBuilder] {p * 100f:0}% – {m}")
				};

				var result = await Builder.Build(data);

				if (result.IsFailed) {
					Logger.LogError($"[ExternalBuilder] Build failed: {result.Message}");
					EditorApplication.Exit(1);
				} else {
					Logger.Log($"[ExternalBuilder] Build succeeded: {result.Output}");
					EditorApplication.Exit(0);
				}
			} catch (Exception e) {
				Logger.LogError($"[ExternalBuilder] Unexpected error: {e}");
				EditorApplication.Exit(1);
			}
		}

		private static string GetArg(string[] args, string name) {
			var idx = Array.IndexOf(args, name);
			return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
		}
	}
}
