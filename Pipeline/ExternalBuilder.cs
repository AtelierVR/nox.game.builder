using System;
using Cysharp.Threading.Tasks;
using Nox.CCK.Attributes;
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
			StartGameBuild();
		}

		/// <summary>
		/// Builds only a single mod (DLLs + AssetBundles), without building the player.
		/// Usage: -executeMethod Nox.GameBuilder.Pipeline.ExternalBuilder.BuildMod
		///        -noxMod nox.network
		///        -noxOutputPath build/nox.network
		///        -noxTargetPlatform StandaloneWindows64
		/// </summary>
		public static void BuildMod() {
			SessionState.SetBool(KeyDone, false);
			SessionState.SetBool(KeyModRequested, true);
			StartBuildMod();
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
				StartBuildMod();
			else
				StartGameBuild();
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

				var args  = ArgsParser.Parse();
				var modIds = args.GetList("noxMod");
				var targets = args.GetDictionary("noxOutput");
				var flags   = ModBuildFlags.None;

				if (modIds.Count == 0) {
					Logger.LogError("Missing --noxMod argument (comma-separated or repeated).", tag: nameof(ExternalBuilder));
					SessionState.SetBool(KeyDone, true);
					EditorApplication.Exit(1);
					return;
				}
				if (targets.Count == 0) {
					Logger.LogError("Missing --noxOutput argument (e.g. StandaloneWindows64=path).", tag: nameof(ExternalBuilder));
					SessionState.SetBool(KeyDone, true);
					EditorApplication.Exit(1);
					return;
				}

				// Discover and load all mods so ModManager.Mods is populated
				await ModManager.LoadMods();

				// Invoke all registered build steps for ModBuild (once)
				NoxInvokableAttribute.Invoke("build:any", modIds, targets, flags);
				NoxInvokableAttribute.Invoke("build:mod", modIds, targets, flags);
				NoxInvokableAttribute.Invoke("build:mod:start", modIds, targets, flags);

				int exitCode = 0;
				foreach (var (platStr, path) in targets) {
					if (!Enum.TryParse<BuildTarget>(platStr, out var buildTarget)) {
						Logger.LogWarning($"Unknown build target '{platStr}', skipping.", tag: nameof(ExternalBuilder));
						continue;
					}
					var plat = buildTarget.GetPlatform();
					if (plat == Platform.None) {
						Logger.LogWarning($"Unsupported platform '{platStr}', skipping.", tag: nameof(ExternalBuilder));
						continue;
					}
					Logger.Log($"Building for {plat} → {path}", tag: nameof(ExternalBuilder));
					NoxInvokableAttribute.Invoke("build:mod:platform:start", modIds, plat, path);
					var data = new ModBuildData {
						ModIds     = modIds.ToArray(),
						OutputPath = path,
						Target     = plat,
						Flags      = flags,
						ProgressCallback = (p, m) => Logger.Log($"{p * 100f:0}% – {m}", tag: nameof(ExternalBuilder))
					};
					var result = await ModBuild.Build(data);
					if (result.IsFailed) {
						Logger.LogError($"BuildMod [{plat}] failed: {result.Message}", tag: nameof(ExternalBuilder));
						exitCode = 1;
					} else {
						Logger.Log($"BuildMod [{plat}] succeeded: {result.Output}", tag: nameof(ExternalBuilder));
					}
					NoxInvokableAttribute.Invoke("build:mod:platform:done", modIds, plat, path, result);
				}
				NoxInvokableAttribute.Invoke("build:mod:done", modIds, targets, exitCode);
				SessionState.SetBool(KeyDone, true);
				EditorApplication.Exit(exitCode);
			} catch (Exception e) {
				Logger.LogError($"BuildMod failed: {e}", tag: nameof(ExternalBuilder));
				SessionState.SetBool(KeyDone, true);
				EditorApplication.Exit(2);
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

				var args           = ArgsParser.Parse();
				var output         = args.Get("noxOutputPath") ?? "build";
				var buildName      = args.Get("noxBuildName") ?? Application.productName;
				var platform       = PlatformExtensions.CurrentPlatform;
				var releaseVersion = args.Get("noxReleaseVersion");
				var releaseChannel = args.Get("noxReleaseChannel");

				var debug = string.Join("\n", new[] {
					$"  platform       = {platform.GetPlatformName()}",
					$"  output         = {output}",
					$"  buildName      = {buildName}",
					$"  releaseVersion = {releaseVersion ?? "(not set)"}",
					$"  releaseChannel = {releaseChannel ?? "(not set)"}",
					$"  args           = {args}"
				});

				Logger.Log($"Starting external build with parameters:\n{debug}", tag: nameof(ExternalBuilder));

				// Apply release version to PlayerSettings only if it actually changed.
				if (!string.IsNullOrEmpty(releaseVersion) && PlayerSettings.bundleVersion != releaseVersion)
					PlayerSettings.bundleVersion = releaseVersion;

				// Discover and load all mods (kernel mods will be filtered inside Builder)
				await ModManager.LoadMods();

				// Invoke all registered build steps for GameBuild
				NoxInvokableAttribute.Invoke("build:any");
				NoxInvokableAttribute.Invoke("build:game");

				var flags = GameBuildFlags.None;
				if (args.GetBool("noxAutoConfirmClearOutput"))
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
				EditorApplication.Exit(2);
			} finally {
				SessionState.SetBool(KeyRunning, false);
			}
		}

	}
}
