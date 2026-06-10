using System;
using System.Linq;
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
		private const string KeyRequested    = "Nox.ExternalBuilder.Requested";
		private const string KeyRunning      = "Nox.ExternalBuilder.Running";
		private const string KeyDone         = "Nox.ExternalBuilder.Done";
		private const string KeyModRequested = "Nox.ExternalBuilder.ModRequested";

		/// <summary>
		/// Called by Unity's -executeMethod mechanism.
		/// Marks this session as a build job so that OnAfterDomainReload can
		/// (re-)schedule the build after every subsequent domain reload.
		/// </summary>
		public static void GameBuild() {
			SessionState.SetBool(KeyDone, false);
			SessionState.SetBool(KeyRequested, true);
			EditorApplication.delayCall += StartGameBuild;
		}

		/// <summary>
		/// Builds only a single mod (DLLs + AssetBundles), without building the player.
		/// Usage: -executeMethod Nox.GameBuilder.Pipeline.ExternalBuilder.BuildMod
		///        -noxModToBuild nox.network
		///        -noxOutputPath build/nox.network
		///        -noxTargetPlatform StandaloneWindows64
		/// </summary>
		public static void BuildMod() {
			SessionState.SetBool(KeyDone, false);
			SessionState.SetBool(KeyModRequested, true);
			EditorApplication.delayCall += StartBuildMod;
		}

		/// <summary>
		/// Called automatically after every domain reload.
		/// If GameBuild() or BuildMod() was invoked earlier this session, re-schedules.
		/// </summary>
		[InitializeOnLoadMethod]
		static void OnAfterDomainReload() {
			bool isFullBuild = SessionState.GetBool(KeyRequested, false);
			bool isModBuild  = SessionState.GetBool(KeyModRequested, false);

			if (!isFullBuild && !isModBuild) return;
			if (SessionState.GetBool(KeyDone, false)) return;

			// A domain reload destroyed any in-flight async task — allow a fresh start.
			SessionState.SetBool(KeyRunning, false);

			if (isModBuild)
				EditorApplication.delayCall += StartBuildMod;
			else
				EditorApplication.delayCall += StartGameBuild;
		}

		static void StartGameBuild() {
			if (SessionState.GetBool(KeyRunning, false)) return;
			SessionState.SetBool(KeyRunning, true);
			RunGameBuildAsync().Forget();
		}

		static void StartBuildMod() {
			if (SessionState.GetBool(KeyRunning, false)) return;
			SessionState.SetBool(KeyRunning, true);
			RunBuildModAsync().Forget();
		}

		// ═══════════════════════════════════════════════════════════════
		// BuildMod — DLLs + AssetBundles only, no player build
		// ═══════════════════════════════════════════════════════════════

		private static async UniTaskVoid RunBuildModAsync() {
			try {
				await UniTask.NextFrame();

				var args      = Environment.GetCommandLineArgs();
				var modId     = GetArg(args, "-noxModToBuild");
				var output    = GetArg(args, "-noxOutputPath") ?? "build/mods";
				var targetStr = GetArg(args, "-noxTargetPlatform") ?? "StandaloneWindows64";

				if (string.IsNullOrEmpty(modId)) {
					Logger.LogError("Missing -noxModToBuild argument (comma-separated for multiple).", tag: nameof(ExternalBuilder));
					SessionState.SetBool(KeyDone, true);
					EditorApplication.Exit(1);
					return;
				}

				var platform = PlatformExtensions.CurrentPlatform;
				if (!string.IsNullOrEmpty(targetStr))
					try { platform = (Platform)Enum.Parse(typeof(Platform), targetStr); } catch { }

				// Discover and load all mods so ModManager.Mods is populated
				await ModManager.LoadMods();

				var data = new ModBuildData {
					ModIds     = modId.Split(',').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToArray(),
					OutputPath = output,
					Target     = platform,
					ProgressCallback = (p, m) => Logger.Log($"{p * 100f:0}% – {m}", tag: nameof(ExternalBuilder))
				};

				var result = await ModBuild.Build(data);

				if (result.IsFailed) {
					Logger.LogError($"BuildMod failed: {result.Message}", tag: nameof(ExternalBuilder));
					SessionState.SetBool(KeyDone, true);
					EditorApplication.Exit(1);
				} else {
					Logger.Log($"BuildMod succeeded: {result.Output}", tag: nameof(ExternalBuilder));
					SessionState.SetBool(KeyDone, true);
					EditorApplication.Exit(0);
				}
			} catch (Exception e) {
				Logger.LogError($"BuildMod failed: {e}", tag: nameof(ExternalBuilder));
				SessionState.SetBool(KeyDone, true);
				EditorApplication.Exit(1);
			} finally {
				SessionState.SetBool(KeyRunning, false);
			}
		}

		// ═══════════════════════════════════════════════════════════════
		// Build — full player + mods + asset bundles (existing)
		// ═══════════════════════════════════════════════════════════════

		private static async UniTaskVoid RunGameBuildAsync() {
			try {
				// One frame yield to let any remaining deferred calls flush
				await UniTask.NextFrame();

				var args            = Environment.GetCommandLineArgs();
				// -noxOutputPath is always a directory; GameBuild appends BuildName + extension.
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

				var flags = GameBuildFlags.None;
				if (Array.IndexOf(args, "-noxAutoConfirmClearOutput") >= 0)
					flags |= GameBuildFlags.AutoConfirmClearOutput;

				var data = new GameBuildData {
					OutputPath = output,
					BuildName  = buildName,
					Target     = platform,
					Flags      = flags,
					Mods       = ModManager.GetMods(),
					Version    = releaseVersion,
					Channel    = releaseChannel,
					ProgressCallback = (p, m) => Logger.Log($"{p * 100f:0}% – {m}", tag: nameof(ExternalBuilder))
				};

				var result = await Pipeline.GameBuild.Build(data);

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
