using System;
using System.IO;
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
	///   -customBuildName  &lt;executable name&gt;
	///
	/// Pass additionally:
	///   -noxOutputPath    &lt;output directory&gt;  (always a folder; result: dir/name.ext)
	/// </summary>
	public static class ExternalBuilder {
		/// <summary>
		/// Synchronous entry point called by Unity (-executeMethod).
		/// Kicks off the async build and lets the Unity event loop process it.
		/// Do NOT pass -quit to Unity; this method calls EditorApplication.Exit itself.
		/// </summary>
		public static void Build() 
			=> RunBuildAsync().Forget();
		

		private static async UniTaskVoid RunBuildAsync() {
			try {
				var args            = Environment.GetCommandLineArgs();
				// -noxOutputPath is always a directory; Builder appends BuildName + extension.
				var output = GetArg(args, "-noxOutputPath") ?? "build";
				var buildName       = GetArg(args, "-customBuildName") ?? Application.productName;
				var platform       = PlatformExtensions.CurrentPlatform;
				var releaseVersion = GetArg(args, "-noxReleaseVersion");
				var releaseChannel = GetArg(args, "-noxReleaseChannel");

				var debug = string.Join("\n", new[] {
					$"  platform       = {platform.GetPlatformName()}",
					$"  output         = {output}",
					$"  buildName      = {buildName}",
					$"  releaseVersion = {releaseVersion ?? "(not set)"}",
					$"  releaseChannel = {releaseChannel ?? "(not set)"}",
					$"  args           = {string.Join(" ", args)}"
				});

				Logger.Log($"Starting external build with parameters:\n{debug}", tag: nameof(ExternalBuilder));

				// Apply release version to PlayerSettings so it is embedded in the binary
				if (!string.IsNullOrEmpty(releaseVersion))
					PlayerSettings.bundleVersion = releaseVersion;

				// Discover and load all mods (kernel mods will be filtered inside Builder)
				await ModManager.LoadMods();

				var data = new BuildData {
					OutputPath = output,
					BuildName  = buildName,
					Target     = platform,
					Mods       = ModManager.GetMods(),
					Version    = releaseVersion,
					Channel    = releaseChannel,
					ProgressCallback = (p, m) => Logger.Log($"{p * 100f:0}% – {m}", tag: nameof(ExternalBuilder))
				};

				var result = await Builder.Build(data);

				if (result.IsFailed) {
					Logger.LogError($"Build failed: {result.Message}", tag: nameof(ExternalBuilder));
					EditorApplication.Exit(1);
				} else {
					Logger.Log($"Build succeeded: {result.Output}", tag: nameof(ExternalBuilder));
					EditorApplication.Exit(0);
				}
			} catch (Exception e) {
				Logger.LogError($"Unexpected error: {e}", tag: nameof(ExternalBuilder));
				EditorApplication.Exit(1);
			}
		}

		private static string GetArg(string[] args, string name) {
			var idx = Array.IndexOf(args, name);
			return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
		}
	}
}
