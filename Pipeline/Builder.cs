using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Nox.CCK.Mods;
using Nox.CCK.Mods.Metadata;
using Nox.CCK.Utils;
using UnityEditor;
using UnityEngine;
using UnityEngine.Events;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.GameBuilder.Pipeline {
	public static class Builder {
		public static bool IsBuilding;

		public static readonly UnityEvent<float, string> OnBuildProgress = new();
		public static readonly UnityEvent<BuildResult>   OnBuildFinished = new();
		public static readonly UnityEvent<BuildData>     OnBuildStarted  = new();

		public static async UniTask<BuildResult> Build(BuildData data) {
			// Wrap user progress callback to also emit UnityEvent
			var userProgress = data.ProgressCallback;
			data.ProgressCallback = (p, m) => {
				try {
					OnBuildProgress.Invoke(p, m);
				} catch {
					/* ignore listener errors */
				}

				try {
					userProgress?.Invoke(p, m);
				} catch {
					/* ignore user callback errors */
				}
			};

			// Notify build start
			try {
				OnBuildStarted.Invoke(data);
			} catch {
				/* ignore listener errors */
			}

			BuildResult Finish(BuildResult r) {
				try {
					OnBuildFinished.Invoke(r);
				} catch {
					/* ignore listener errors */
				}

				return r;
			}

			if (IsBuilding) {
				return Finish(
					new BuildResult {
						Type    = BuildResultType.AlreadyBuilding,
						Message = "A build is already in progress."
					}
				);
			}

			if (EditorApplication.isCompiling) {
				return Finish(
					new BuildResult {
						Type    = BuildResultType.EditorCompiling,
						Message = "The editor is currently compiling scripts."
					}
				);
			}

			if (EditorApplication.isPlaying) {
				return Finish(
					new BuildResult {
						Type    = BuildResultType.EditorPlaying,
						Message = "The editor is currently in play mode."
					}
				);
			}

			IsBuilding = true;

			try {
				// Use default build name if not provided
				if (string.IsNullOrEmpty(data.BuildName))
					data.BuildName = Application.productName;

				// Use current platform if not provided or None
				if (data.Target == Platform.None)
					data.Target = PlatformExtensions.CurrentPlatform;

				// Filter mods to only include kernel mods
				data.Mods = GetKernelMods(data.Mods);

				if (!data.Target.IsSupported())
					return Finish(
						new BuildResult {
							Type    = BuildResultType.UnsupportedTarget,
							Message = $"The platform {data.Target.GetPlatformName()} is not supported on this editor version."
						}
					);

				data.ProgressCallback(0.1f, "Preparing build...");
				await UniTask.Yield();

				if (File.Exists(data.OutputPath))
					File.Delete(data.OutputPath);

				PrepareOutputDirectory(data);

				var scenes = GetScenesToBuild(data.Mods);

				var buildPlayerOptions = new BuildPlayerOptions {
					scenes           = scenes,
					locationPathName = Path.Combine(data.OutputPath, data.BuildName + (data.Target == Platform.Windows ? ".exe" : "")),
					options          = data.BuildOptions,
					target           = data.Target.GetBuildTarget()
				};

				data.ProgressCallback(0.3f, "Starting Unity build...");
				await UniTask.Yield();

				var report  = BuildPipeline.BuildPlayer(buildPlayerOptions);
				var summary = report.summary;

				if (summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
					return Finish(
						new BuildResult {
							Type    = BuildResultType.Failed,
							Message = $"Build failed with {summary.totalErrors} errors."
						}
					);

				// Build Mods and Game Data
				if (data.Mods is { Length: > 0 }) {
					data.ProgressCallback(0.5f, "Building mods...");
					await UniTask.Delay(100);

					var outputData = Path.Combine(data.OutputPath, data.BuildName + "_Data", "Nox");
					var gameData   = Path.Combine(outputData, "game_data.json");

					if (!Directory.Exists(outputData))
						Directory.CreateDirectory(outputData);

					var resultAssets = BuildAssets.BuildAsAssetBundles(data.Mods, data.Target, outputData);

					if (resultAssets == null)
						return Finish(
							new BuildResult {
								Type    = BuildResultType.Failed,
								Message = "Failed to build asset bundles."
							}
						);

					var m = new JArray();

					for (var i = 0; i < data.Mods.Length; i++) {
						var mod  = data.Mods[i];
						var meta = mod.GetMetadata();

						data.ProgressCallback(0.5f + 0.4f * ((float)i / data.Mods.Length), $"Processing mod {meta.GetId()}...");
						await UniTask.Yield();

						var obj = meta.ToObject();

						obj["kernel"] = new JObject {
							["active"]      = meta.GetCustom("kernel", mod.GetModType() == "kernel"),
							["base_path"]   = GetPath(mod.GetData<string>("folder")),
							["assets_path"] = GetPath(mod.GetData<string>("assets")),
							["definition"]  = GetPath(mod.GetData<string>("definition")),
							["manifest"]    = GetPath(mod.GetData<string>("manifest"))
						};

						var assetResult = resultAssets
							.FirstOrDefault(r => r.mod.GetMetadata().Match(meta.GetId()));

						JArray assetObj = new();
						if (assetResult != null)
							foreach (var asset in assetResult.outputs)
								if (File.Exists(asset)) {
									var bundle = AssetBundle.LoadFromFile(asset);
									if (!bundle) continue;
									assetObj.Add(
										new JObject {
											["name"] = Path.GetFileName(asset),
											["file"] = Path.Combine(Path.GetRelativePath(outputData, asset))
												.Replace("\\", "/")
												.ToLower(),
											["assets"] = new JArray(bundle.GetAllAssetNames().Select(a => a.ToLower())),
											["scenes"] = new JArray(bundle.GetAllScenePaths().Select(a => a.ToLower()))
										}
									);
									bundle.Unload(true);
								}

						obj["kernel"]["assets"] = assetObj;

						m.Add(obj);
					}

					data.ProgressCallback(0.95f, "Saving game data...");
					await UniTask.Yield();

					await File.WriteAllTextAsync(
						gameData, new JObject {
							["mods"] = m,
							["engine"] = new JObject {
								["name"]    = EngineExtensions.CurrentEngine.ToString(),
								["version"] = EngineExtensions.CurrentVersion.ToString()
							},
							["platform"] = data.Target.GetPlatformName()
						}.ToString()
					);
				}

				data.ProgressCallback(1f, "Build completed successfully.");
				return Finish(
					new BuildResult {
						Type    = BuildResultType.Success,
						Output  = summary.outputPath,
						Message = "Build completed successfully."
					}
				);
			} catch (Exception e) {
				Logger.LogError($"Build failed: {e}");
				return Finish(
					new BuildResult {
						Type    = BuildResultType.Failed,
						Message = $"Build failed with exception: {e.Message}"
					}
				);
			} finally {
				IsBuilding = false;
			}
		}

		public static string[] GetScenesToBuild(IMod[] mods) {
			var list = new List<string>();

			try {
				if (mods != null) {
					foreach (var mod in mods) {
						var path = mod.GetData("folder", "");
						if (string.IsNullOrEmpty(path)) continue;
						// Case-insensitive lookup to support both Linux and Windows
						var t = Directory.Exists(path)
							? Directory.GetDirectories(path)
								.FirstOrDefault(d => string.Equals(Path.GetFileName(d), "BuildScenes", StringComparison.OrdinalIgnoreCase))
							: null;
						if (string.IsNullOrEmpty(t)) continue;
						Logger.LogDebug($"Searching for scenes: {t}");
						var files = Directory.GetFiles(t, "*.unity", SearchOption.AllDirectories)
							.Select(GetPath)
							.Where(f => !string.IsNullOrEmpty(f))
							.ToArray();
						Logger.LogDebug($"Found {string.Join(", ", files)}");
						list.AddRange(files);
					}
				}

				list.Sort((a, b) => a.EndsWith("main.unity") ? -1 : 1);
			} catch (Exception ex) {
				Logger.LogWarning(new Exception("Error getting build scenes from mods", ex));
			}

			return list.ToArray();
		}

		public static IMod[] GetKernelMods(IMod[] mods)
			=> mods
				.Where(m => m.GetModType() == "kernel")
				.ToArray();

		private static void PrepareOutputDirectory(BuildData data) {
			if (!Directory.Exists(data.OutputPath)) {
				Directory.CreateDirectory(data.OutputPath);
				return;
			}

			if (IsOutputEmpty(data.OutputPath) || AllowClearOutput(data)) {
				if (!IsOutputEmpty(data.OutputPath)) {
					Directory.Delete(data.OutputPath, true);
					Directory.CreateDirectory(data.OutputPath);
				}
				return;
			}

			throw new InvalidOperationException(
				$"Output folder is not empty: {data.OutputPath}. " +
				"Pass BuildFlags.AutoConfirmClearOutput or clear the folder manually."
			);
		}

		private static bool IsOutputEmpty(string path)
			=> Directory.GetFileSystemEntries(path).Length == 0;

		private static bool AllowClearOutput(BuildData data) {
			if ((data.Flags & BuildFlags.AutoConfirmClearOutput) != 0)
				return true;

			// Batch mode has no UI — cannot ask.
			if (Application.isBatchMode)
				return false;

			return Logger.OpenDialog(
				"Output folder is not empty",
				$"The output folder already contains files:\n{data.OutputPath}\n\nClear it before building?",
				"Yes, clear it",
				"No, cancel"
			);
		}

		private static string GetPath(string path) {
			if (string.IsNullOrEmpty(path)) return null;
			var fullPath    = Path.GetFullPath(path).Replace('\\', '/');
			var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..")).Replace('\\', '/').TrimEnd('/') + "/";
			if (!fullPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
				return null;
			return fullPath[projectRoot.Length..];
		}
	}
}