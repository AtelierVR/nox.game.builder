using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Nox.CCK.Mods;
using Nox.CCK.Mods.Metadata;
using Nox.CCK.Utils;
using Nox.GameBuilder.Pipeline.Utils;
using Nox.ModLoader;
using TypedReference = Nox.ModLoader.Typing.Reference;
using TypedAsset     = Nox.ModLoader.Typing.Asset;
using UnityEngine;
using UnityEngine.Events;
using UnityEditor;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.GameBuilder.Pipeline {
	public static class ModBuild {
		public static bool IsBuilding;

		public static readonly UnityEvent<float, string> OnModBuildProgress = new();
		public static readonly UnityEvent<BuildResult>   OnModBuildFinished = new();
		public static readonly UnityEvent<ModBuildData>  OnModBuildStarted  = new();

		public static async UniTask<BuildResult> Build(ModBuildData data) {
			var userProgress = data.ProgressCallback;
			data.ProgressCallback = (p, m) => {
				try { OnModBuildProgress.Invoke(p, m); } catch { }
				try { userProgress?.Invoke(p, m); } catch { }
			};

			try { OnModBuildStarted.Invoke(data); } catch { }

			BuildResult Finish(BuildResult r) {
				try { OnModBuildFinished.Invoke(r); } catch { }
				return r;
			}

			if (IsBuilding)
				return Finish(new BuildResult { Type = BuildResultType.AlreadyBuilding, Message = "A mod build is already in progress." });

			if (EditorApplication.isCompiling)
				return Finish(new BuildResult { Type = BuildResultType.EditorCompiling, Message = "The editor is currently compiling scripts." });

			if (EditorApplication.isPlaying)
				return Finish(new BuildResult { Type = BuildResultType.EditorPlaying, Message = "The editor is currently in play mode." });

			if (data.ModIds == null || data.ModIds.Length == 0)
				return Finish(new BuildResult { Type = BuildResultType.Failed, Message = "No mod IDs specified." });

			IsBuilding = true;

			var platform   = data.Target;
			var playerTemp = Path.Combine(Path.GetTempPath(), "nox_player_build_" + Guid.NewGuid().ToString("N"));
			var managedDir = ""; // will be set after player build

			try {
				// ── 0. Prepare output ────────────────────────────
				data.ProgressCallback(0.02f, "Preparing output...");
				await UniTask.Yield();
				PrepareOutputDirectory(data.OutputPath);

				// ── 1. Load mods ──────────────────────────────────
				data.ProgressCallback(0.05f, "Loading mods...");
				await UniTask.Yield();
				var allMods = ModManager.Mods;

				var targets = new List<(IMod mod, Nox.ModLoader.Typing.ModMetadata meta, string folder)>();
				foreach (var id in data.ModIds) {
					var m = allMods.FirstOrDefault(x => x.GetMetadata().GetId().Equals(id, StringComparison.OrdinalIgnoreCase));
					if (m == null)
						return Finish(new BuildResult { Type = BuildResultType.Failed, Message = $"Mod not found: {id}" });
					targets.Add((m, (Nox.ModLoader.Typing.ModMetadata)m.GetMetadata(), m.GetData<string>("folder")));
				}

				// ── 1. Build player (to get platform-correct assemblies) ─
				data.ProgressCallback(0.1f, "Building player...");

				// Disable managed stripping to avoid UnityLinker crashes in CI
				var unityTarget = platform.GetBuildTarget();
				var targetGroup = BuildPipeline.GetBuildTargetGroup(unityTarget);
				var prevStripping = PlayerSettings.GetManagedStrippingLevel(targetGroup);
				PlayerSettings.SetManagedStrippingLevel(targetGroup, ManagedStrippingLevel.Disabled);

				var scenes = GameBuild.GetScenesToBuild(allMods.ToArray());
				Directory.CreateDirectory(playerTemp);

				var playerName = Application.productName;
				var buildOptions = new BuildPlayerOptions {
					scenes           = scenes,
					locationPathName = Path.Combine(playerTemp, playerName + (platform == Platform.Windows ? ".exe" : "")),
					options          = data.BuildOptions,
					target           = platform.GetBuildTarget()
				};

				var buildTcs = new UniTaskCompletionSource<UnityEditor.Build.Reporting.BuildReport>();
				EditorApplication.delayCall += () => {
					try {
						var r = BuildPipeline.BuildPlayer(buildOptions);
						buildTcs.TrySetResult(r);
					} catch (Exception ex) {
						buildTcs.TrySetException(ex);
					}
				};
				var report = await buildTcs.Task;
				PlayerSettings.SetManagedStrippingLevel(targetGroup, prevStripping);
				if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
					return Finish(new BuildResult { Type = BuildResultType.Failed, Message = $"Player build failed with {report.summary.totalErrors} errors." });

				managedDir = Path.Combine(playerTemp, playerName + "_Data", "Managed");
				var ext = Library.GetExtension(platform);

				// ── 3. Process each mod ───────────────────────────
				var totalDlls    = 0;
				var totalBundles = 0;
				var total        = targets.Count;

				for (int i = 0; i < total; i++) {
					var (mod, meta, modFolder) = targets[i];
					var modOutput = Path.Combine(data.OutputPath, meta.GetId());
					Directory.CreateDirectory(modOutput);

					var baseProgress = 0.3f + (0.6f * i / total);
					data.ProgressCallback(baseProgress, $"Processing {meta.GetId()}...");
					await UniTask.Yield();

					// ── 2a. Build asset bundles ────────────────────
					var bundleTemp = Path.Combine(Path.GetTempPath(), "nox_bundle_" + Guid.NewGuid().ToString("N"));
					Directory.CreateDirectory(bundleTemp);
					var assetsDir  = Path.Combine(modOutput, "assets");
					var assetResults = BuildAssets.BuildAsAssetBundles(new[] { mod }, platform, bundleTemp);

					if (assetResults == null) {
						Logger.LogWarning($"Asset bundle build failed for {meta.GetId()}, continuing without bundles.", tag: nameof(ModBuild));
					} else {
						Directory.CreateDirectory(assetsDir);
						foreach (var src in assetResults.SelectMany(r => r.outputs))
							if (File.Exists(src)) {
								var dest = Path.Combine(assetsDir, Path.GetFileName(src));
								File.Move(src, dest);
							}
						try { Directory.Delete(bundleTemp, true); } catch { }
					}

					// ── 2b. Copy managed assemblies ────────────────
					var libDir     = Path.Combine(modOutput, "lib");
					var copiedDlls = new List<(string path, string hash)>();

					if (!Directory.Exists(managedDir)) {
						Logger.LogWarning($"Managed directory not found: {managedDir}", tag: nameof(ModBuild));
					} else if (Directory.Exists(modFolder))
						foreach (var asmdefFile in Directory.GetFiles(modFolder, "*.asmdef", SearchOption.AllDirectories))
							try {
								var json    = JObject.Parse(File.ReadAllText(asmdefFile));
								var asmName = json["name"]?.Value<string>();
								if (string.IsNullOrEmpty(asmName)) continue;

								var srcFile = asmName + ".dll"; // managed assemblies are always .dll
								var srcPath = Path.Combine(managedDir, srcFile);
								if (!File.Exists(srcPath)) {
									Logger.LogWarning($"Assembly not found in player: {srcFile}", tag: nameof(ModBuild));
									continue;
								}

								Directory.CreateDirectory(libDir);
								var destPath = Path.Combine(libDir, srcFile);
								File.Copy(srcPath, destPath, true);
								copiedDlls.Add(("lib/" + srcFile, Hashing.HashFile(destPath)));
							} catch (Exception ex) {
								Logger.LogWarning($"Failed to process asmdef {asmdefFile}: {ex.Message}", tag: nameof(ModBuild));
							}

					// ── 2c. Write manifest ─────────────────────────
					var bundleAssets = new JArray();
					if (Directory.Exists(assetsDir)) {
						foreach (var f in Directory.GetFiles(assetsDir)) {
							var bundle = AssetBundle.LoadFromFile(f);
							if (!bundle) continue;
							var fn   = Path.GetFileName(f);
							var hash = Hashing.HashFile(f);
							bundleAssets.Add(new TypedAsset {
								Name   = fn,
								File   = "assets/" + fn,
								Hash   = "sha256:" + hash,
								Assets = bundle.GetAllAssetNames().Select(a => a.ToLower()).ToArray(),
								Scenes = bundle.GetAllScenePaths().Select(a => a.ToLower()).ToArray(),
							}.ToJson());
							bundle.Unload(true);
						}
					}

					var refs = new JArray();
					foreach (var (dll, hash) in copiedDlls) {
						refs.Add(new TypedReference {
							Name = Path.GetFileNameWithoutExtension(dll),
							File = dll,
							Type = "library",
							Hash = "sha256:" + hash,
							Tags = new[] {
								"platform:" + platform.GetPlatformName(),
								"engine:" + EngineExtensions.CurrentEngine.GetEngineName()
									+ ":" + EngineExtensions.CurrentVersion,
							},
						}.ToJson());
					}

					// Copy native plugins and add to references
					var pluginsDir = Path.Combine(modFolder ?? "", "Plugins");
					if (Directory.Exists(pluginsDir)) {
						var pluginsOutput = Path.Combine(modOutput, "plugins");
						Directory.CreateDirectory(pluginsOutput);

						foreach (var srcFile in Directory.GetFiles(pluginsDir, "*", SearchOption.AllDirectories)) {
							if (srcFile.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;

							var srcRelative = srcFile[(pluginsDir.Length + 1)..].Replace('\\', '/');
							var fileName    = Path.GetFileName(srcFile);

							// Resolve platforms and architecture
							var platNames = PluginUtils.ReadPlatforms(srcFile)
							             ?? new[] { PluginUtils.InferPlatform(srcRelative, platform.GetPlatformName()) };
							var platName  = platNames[0];
							var archName  = PluginUtils.ReadArchitecture(srcFile)
							             ?? PluginUtils.InferArchitecture(srcRelative);

							var plat = platName.GetPlatformFromName();
							var arch = archName?.GetArchitectureFromName() ?? Architecture.None;

							// Build destination subfolder (null → root of plugins/)
							var subFolders = Library.GetSubFolders(plat, arch);
							var subFolder = subFolders.Length > 0 ? subFolders[0] : null;
							var destRelative = subFolder != null ? subFolder + "/" + fileName : fileName;
							var destFile = Path.Combine(pluginsOutput, destRelative);
							Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
							File.Copy(srcFile, destFile, true);

							refs.Add(new TypedReference {
								Name        = fileName,
								File        = "plugins/" + destRelative,
								Type        = "plugin",
								Hash        = "sha256:" + Hashing.HashFile(destFile),
								Tags = BuildTags(platNames, archName),
							}.ToJson());
						}
					}


					var manifest = meta.ToObject(ModMetadataFormat.EntryPointObject);
					manifest["references"] = refs;
					manifest["assets"]     = bundleAssets;
					File.WriteAllText(Path.Combine(modOutput, "nox.mod.json"), manifest.ToString());

					// Copy package.json
					var pkgJson = Path.Combine(modFolder ?? "", "package.json");
					if (File.Exists(pkgJson))
						File.Copy(pkgJson, Path.Combine(modOutput, "package.json"), true);

					totalDlls    += copiedDlls.Count;
					totalBundles += bundleAssets.Count;
					Logger.Log($"  Built {meta.GetId()}: {copiedDlls.Count} DLLs, {bundleAssets.Count} bundles", tag: nameof(ModBuild));
				}

				// Cleanup
				try { Directory.Delete(playerTemp, true); } catch { }

				data.ProgressCallback(1f, "Mod build completed.");
				return Finish(new BuildResult {
					Type    = BuildResultType.Success,
					Output  = Path.GetFullPath(data.OutputPath),
					Message = $"Built {total} mod(s): {totalDlls} DLLs, {totalBundles} bundles"
				});
			} catch (Exception e) {
				Logger.LogError(e);
				try { if (Directory.Exists(playerTemp)) Directory.Delete(playerTemp, true); } catch { }
				return Finish(new BuildResult { Type = BuildResultType.Failed, Message = $"Mod build failed: {e.Message}" });
			} finally {
				IsBuilding = false;
			}
		}

		static void PrepareOutputDirectory(string path) {
			if (!Directory.Exists(path)) {
				Directory.CreateDirectory(path);
				return;
			}

			if (IsOutputEmpty(path) || AllowClearOutput()) {
				if (!IsOutputEmpty(path)) {
					Directory.Delete(path, true);
					Directory.CreateDirectory(path);
				}
				return;
			}

			throw new InvalidOperationException(
				$"Output folder is not empty: {path}. " +
				"Clear the folder manually before building."
			);
		}

		static bool IsOutputEmpty(string path)
			=> Directory.GetFileSystemEntries(path).Length == 0;

		static bool AllowClearOutput() {
			if (Application.isBatchMode)
				return false;

			return Logger.OpenDialog(
				"Output folder is not empty",
				"The output folder already contains files.\nClear it before building?",
				"Yes, clear it",
				"No, cancel"
			);
		}

		static string[] BuildTags(string[] platforms, string arch) {
			var constraints = new List<string>();
			foreach (var p in platforms)
				constraints.Add("platform:" + p);
			if (!string.IsNullOrEmpty(arch) && arch != "none" && arch != "anycpu")
				constraints.Add("arch:" + arch);
			else
				constraints.Add("arch:any");
			return constraints.ToArray();
		}
	}
}