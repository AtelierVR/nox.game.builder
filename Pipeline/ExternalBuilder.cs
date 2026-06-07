using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
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
		public static void Build() {
			SessionState.SetBool(KeyRequested, true);
			EditorApplication.delayCall += StartBuild;
		}

		/// <summary>
		/// Builds only a single mod (DLLs + AssetBundles), without building the player.
		/// Usage: -executeMethod Nox.GameBuilder.Pipeline.ExternalBuilder.BuildMod
		///        -noxModToBuild nox.network
		///        -noxOutputPath build/nox.network
		///        -noxTargetPlatform StandaloneWindows64
		/// </summary>
		public static void BuildMod() {
			SessionState.SetBool(KeyModRequested, true);
			EditorApplication.delayCall += StartBuildMod;
		}

		/// <summary>
		/// Called automatically after every domain reload.
		/// If Build() or BuildMod() was invoked earlier this session, re-schedules.
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
				EditorApplication.delayCall += StartBuild;
		}

		static void StartBuild() {
			if (SessionState.GetBool(KeyRunning, false)) return;
			SessionState.SetBool(KeyRunning, true);
			RunBuildAsync().Forget();
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

				var args   = Environment.GetCommandLineArgs();
				var modId  = GetArg(args, "-noxModToBuild");
				var output = GetArg(args, "-noxOutputPath") ?? "build/mod";
				var targetStr = GetArg(args, "-noxTargetPlatform") ?? "StandaloneWindows64";

				if (string.IsNullOrEmpty(modId)) {
					Logger.LogError("Missing -noxModToBuild argument. Usage: -noxModToBuild nox.network", tag: nameof(ExternalBuilder));
					SessionState.SetBool(KeyDone, true);
					EditorApplication.Exit(1);
					return;
				}

				Logger.Log($"BuildMod: {modId} -> {output} (target: {targetStr})", tag: nameof(ExternalBuilder));

				// Resolve platform
				var platform = PlatformExtensions.CurrentPlatform;
				// Override if target specified and differs
				if (!string.IsNullOrEmpty(targetStr)) {
					try { platform = (Platform)Enum.Parse(typeof(Platform), targetStr); } catch { }
				}

				// Load all mods
				await ModManager.LoadMods();
				var allMods = ModManager.GetMods();

				// Find the target mod
				var mod = allMods.FirstOrDefault(m =>
					m.GetMetadata().GetId().Equals(modId, StringComparison.OrdinalIgnoreCase));

				if (mod == null) {
					Logger.LogError($"Mod not found: {modId}. Available: {string.Join(", ", allMods.Select(m => m.GetMetadata().GetId()))}", tag: nameof(ExternalBuilder));
					SessionState.SetBool(KeyDone, true);
					EditorApplication.Exit(1);
					return;
				}

				var meta       = mod.GetMetadata();
				var modFolder  = mod.GetData<string>("folder");

				Logger.Log($"Found mod: {meta.GetId()} at {modFolder}", tag: nameof(ExternalBuilder));

				// Ensure output directory
				if (!Directory.Exists(output))
					Directory.CreateDirectory(output);

				// ── 1. Build AssetBundles ──────────────────────────
				var assetResults = BuildAssets.BuildAsAssetBundles(new[] { mod }, platform, output);

				if (assetResults != null && assetResults.Length > 0) {
					Logger.Log($"Built {assetResults.Sum(r => r.outputs.Length)} asset bundle(s)", tag: nameof(ExternalBuilder));
				} else {
					Logger.Log("No asset bundles produced (mod may have no assets folder)", tag: nameof(ExternalBuilder));
				}

				// ── 2. Copy DLLs from Library/ScriptAssemblies/ ────
				var asmDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "ScriptAssemblies"));
				var copiedDlls = new List<string>();

				if (Directory.Exists(modFolder)) {
					var asmdefFiles = Directory.GetFiles(modFolder, "*.asmdef", SearchOption.AllDirectories);
					foreach (var asmdefFile in asmdefFiles) {
						try {
							var json    = JObject.Parse(File.ReadAllText(asmdefFile));
							var asmName = json["name"]?.Value<string>();
							if (string.IsNullOrEmpty(asmName)) continue;

							var dllPath = Path.Combine(asmDir, asmName + ".dll");
							if (!File.Exists(dllPath)) {
								Logger.LogWarning($"DLL not found: {asmName}.dll", tag: nameof(ExternalBuilder));
								continue;
							}

							var destPath = Path.Combine(output, asmName + ".dll");
							File.Copy(dllPath, destPath, true);
							copiedDlls.Add(asmName + ".dll");
							Logger.Log($"  Copied: {asmName}.dll", tag: nameof(ExternalBuilder));
						} catch (Exception ex) {
							Logger.LogWarning($"Failed to process asmdef {asmdefFile}: {ex.Message}", tag: nameof(ExternalBuilder));
						}
					}
				}

				// ── 3. Copy mod manifest ──────────────────────────
				var manifestPath = Path.Combine(modFolder ?? "", "nox.mod.jsonc");
				if (File.Exists(manifestPath)) {
					File.Copy(manifestPath, Path.Combine(output, "nox.mod.jsonc"), true);
					Logger.Log("  Copied: nox.mod.jsonc", tag: nameof(ExternalBuilder));
				}

				// ── 4. Summary ────────────────────────────────────
				var bundleCount = assetResults?.Sum(r => r.outputs.Length) ?? 0;
				Logger.Log($"", tag: nameof(ExternalBuilder));
				Logger.Log($"══ BuildMod complete: {modId} ══", tag: nameof(ExternalBuilder));
				Logger.Log($"  Output : {Path.GetFullPath(output)}", tag: nameof(ExternalBuilder));
				Logger.Log($"  DLLs   : {copiedDlls.Count} ({string.Join(", ", copiedDlls)})", tag: nameof(ExternalBuilder));
				Logger.Log($"  Bundles: {bundleCount}", tag: nameof(ExternalBuilder));

				SessionState.SetBool(KeyDone, true);
				EditorApplication.Exit(0);
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
