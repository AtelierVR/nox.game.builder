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
	///   -customBuildName  &lt;executable name&gt;
	///
	/// Pass additionally:
	///   -noxOutputPath    &lt;output directory&gt;  (always a folder; result: dir/name.ext)
	/// </summary>
	public static class ExternalBuilder {
		private const string KeyRequested = "Nox.ExternalBuilder.Requested";
		private const string KeyRunning   = "Nox.ExternalBuilder.Running";
		private const string KeyDone      = "Nox.ExternalBuilder.Done";

		/// <summary>
		/// Called by Unity's -executeMethod mechanism.
		/// Marks this session as a build job so that OnAfterDomainReload can
		/// (re-)schedule the build after every subsequent domain reload.
		/// </summary>
		public static void Build() {
			SessionState.SetBool(KeyRequested, true);
			EditorApplication.delayCall += StartBuild;
		}

		/// <summary>
		/// Called automatically after every domain reload.
		/// If Build() was invoked earlier this session, re-schedules the build
		/// (any in-flight async task was destroyed by the reload).
		/// </summary>
		[InitializeOnLoadMethod]
		static void OnAfterDomainReload() {
			if (!SessionState.GetBool(KeyRequested, false)) return;
			if (SessionState.GetBool(KeyDone, false)) return;

			// A domain reload destroyed any in-flight async task — allow a fresh start.
			SessionState.SetBool(KeyRunning, false);

			EditorApplication.delayCall += StartBuild;
		}

		static void StartBuild() {
			// Guard: only one concurrent async task at a time.
			if (SessionState.GetBool(KeyRunning, false)) return;
			SessionState.SetBool(KeyRunning, true);
			RunBuildAsync().Forget();
		}

		private static async UniTaskVoid RunBuildAsync() {
			try {
				// One frame yield to let any remaining deferred calls flush
				await UniTask.NextFrame();

				var args            = Environment.GetCommandLineArgs();
				// -noxOutputPath is always a directory; Builder appends BuildName + extension.
				var output      = GetArg(args, "-noxOutputPath") ?? "build";
				// Use -noxBuildName if provided, otherwise fall back to productName.
				// We intentionally ignore -customBuildName (set by game-ci to the target platform name).
				var buildName   = GetArg(args, "-noxBuildName") ?? Application.productName;
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

				// Apply release version to PlayerSettings only if it actually changed.
				// Setting bundleVersion always marks ProjectSettings dirty, which causes
				// Unity to force a synchronous recompile (→ domain reload) inside
				// BuildPipeline.BuildPlayer, destroying our async state machine.
				if (!string.IsNullOrEmpty(releaseVersion) && PlayerSettings.bundleVersion != releaseVersion)
					PlayerSettings.bundleVersion = releaseVersion;

				// Discover and load all mods (kernel mods will be filtered inside Builder)
				await ModManager.LoadMods();

				var flags = BuildFlags.None;
				if (Array.IndexOf(args, "-noxAutoConfirmClearOutput") >= 0)
					flags |= BuildFlags.AutoConfirmClearOutput;

				var data = new BuildData {
					OutputPath = output,
					BuildName  = buildName,
					Target     = platform,
					Flags      = flags,
					Mods       = ModManager.GetMods(),
					Version    = releaseVersion,
					Channel    = releaseChannel,
					ProgressCallback = (p, m) => Logger.Log($"{p * 100f:0}% – {m}", tag: nameof(ExternalBuilder))
				};

				var result = await Builder.Build(data);

				if (result.IsFailed) {
					Logger.LogError($"Build failed: {result.Message}", tag: nameof(ExternalBuilder));
					SessionState.SetBool(KeyDone, true);
					EditorApplication.Exit(1);
				} else {
					Logger.Log($"Build succeeded: {result.Output}", tag: nameof(ExternalBuilder));
					SessionState.SetBool(KeyDone, true);
					EditorApplication.Exit(0);
				}
			} catch (Exception e) {
				Logger.LogError($"Unexpected error: {e}", tag: nameof(ExternalBuilder));
				SessionState.SetBool(KeyDone, true);
				EditorApplication.Exit(1);
			} finally {
				SessionState.SetBool(KeyRunning, false);
			}
		}

		private static string GetArg(string[] args, string name) {
			var idx = Array.IndexOf(args, name);
			return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
		}
	}
}
