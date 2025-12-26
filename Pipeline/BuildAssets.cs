#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nox.CCK.Mods;
using Nox.CCK.Utils;
using UnityEditor;
using UnityEngine;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.GameBuilder.Pipeline {
	public class BuildAssets {
		private static string GetAssetPath(string filePath) {
			// Normaliser le chemin
			filePath = Path.GetFullPath(filePath).Replace('\\', '/');

			// Vérifier si le fichier est dans le dossier Assets
			var assetsPath = Path.GetFullPath(Application.dataPath).Replace('\\', '/');
			if (filePath.StartsWith(assetsPath)) {
				return "Assets" + filePath.Substring(assetsPath.Length);
			}

			// Vérifier si le fichier est dans un package
			var packagesPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Packages")).Replace('\\', '/');
			if (filePath.StartsWith(packagesPath)) {
				return "Packages" + filePath.Substring(packagesPath.Length);
			}

			// Si le chemin n'est ni dans Assets ni dans Packages, on ne peut pas l'utiliser
			Logger.LogWarning($"Asset path not supported: {filePath}");
			return null;
		}

		public static AssetBundleBuildResult[] BuildAsAssetBundles(IMod[] mod, Platform target, string outputfolder) {
			if (!target.IsSupported()) {
				Logger.LogError("Unsupported platform: " + target.GetPlatformName());
				return Array.Empty<AssetBundleBuildResult>();
			}

			List<AssetBundleBuild>       bundles = new();
			List<AssetBundleBuildResult> results = new();

			for (int i = 0; i < mod.Length; i++) {
				var buildname   = mod[i].GetMetadata().GetId();
				var assetfolder = mod[i].GetData<string>("assets");

				if (!Directory.Exists(assetfolder)) continue;

				Logger.Log("Building asset bundles for: " + buildname);

				var scenes = Directory.GetFiles(assetfolder, "*.unity", SearchOption.AllDirectories)
					.Select(f => GetAssetPath(f))
					.Where(p => !string.IsNullOrEmpty(p))
					.ToArray();

				var scriptables = Directory.GetFiles(assetfolder, "*.asset", SearchOption.AllDirectories)
					.Select(f => GetAssetPath(f))
					.Where(p => !string.IsNullOrEmpty(p))
					.ToArray();

				var assets = Directory.GetFiles(assetfolder, "*.*", SearchOption.AllDirectories)
					.Where(f => !f.EndsWith(".meta") && !f.EndsWith(".unity") && !f.EndsWith(".asset") && !f.EndsWith(".cs") && !f.EndsWith(".blend1") && !f.EndsWith(".cginc") && !f.EndsWith(".hlsl") && !f.EndsWith(".svg"))
					.Select(f => GetAssetPath(f))
					.Where(p => !string.IsNullOrEmpty(p))
					.ToArray();


				foreach (var asset in assets)
					Logger.Log($"Asset for {mod[i].GetMetadata().GetId()}: {asset}");

				List<string> resultOutputs = new();

				// Only create bundles if there are assets to include
				if (scenes.Length > 0) {
					var OutSceneName = Guid.NewGuid().ToString().Replace("-", "") + ".scenes";
					bundles.Add(
						new AssetBundleBuild {
							assetBundleName = OutSceneName,
							assetNames      = scenes,
						}
					);
					resultOutputs.Add(Path.Combine(outputfolder, OutSceneName));
				}

				if (assets.Length > 0) {
					var OutAssetName = Guid.NewGuid().ToString().Replace("-", "") + ".assets";
					bundles.Add(
						new AssetBundleBuild {
							assetBundleName = OutAssetName,
							assetNames      = assets,
						}
					);
					resultOutputs.Add(Path.Combine(outputfolder, OutAssetName));
				}

				if (scriptables.Length > 0) {
					var OutScriptableName = Guid.NewGuid().ToString().Replace("-", "") + ".scriptables";
					bundles.Add(
						new AssetBundleBuild {
							assetBundleName = OutScriptableName,
							assetNames      = scriptables,
						}
					);
					resultOutputs.Add(Path.Combine(outputfolder, OutScriptableName));
				}

				if (resultOutputs.Count > 0) {
					results.Add(
						new AssetBundleBuildResult {
							mod     = mod[i],
							outputs = resultOutputs.ToArray()
						}
					);
				}
			}

			// Don't try to build if there are no bundles
			if (bundles.Count == 0) {
				Logger.LogWarning("No asset bundles to build - no assets found in any mod folders");
				return results.ToArray();
			}

			Logger.Log($"Building {bundles.Count} asset bundles...");
			foreach (var bundle in bundles) {
				Logger.Log($"Bundle: {bundle.assetBundleName} with {bundle.assetNames.Length} assets");
			}

			var manifest = BuildPipeline.BuildAssetBundles(
				new() {
					outputPath        = outputfolder,
					targetPlatform    = target.GetBuildTarget(),
					options           = BuildAssetBundleOptions.None | BuildAssetBundleOptions.IgnoreTypeTreeChanges | BuildAssetBundleOptions.RecurseDependencies,
					bundleDefinitions = bundles.ToArray()
				}
			);

			if (manifest == null) {
				Logger.LogError("Failed to build asset bundles");
				return null;
			}

			Logger.Log($"Successfully built {manifest.GetAllAssetBundles().Length} asset bundles");

			var expected = results.SelectMany(r => r.outputs.Select(o => Path.GetFileName(o)));
			if (manifest.GetAllAssetBundles().Length != expected.Count()) {
				Logger.LogWarning("Asset bundles count mismatch");
				Logger.LogWarning($"Expected: ({expected.Count()})[{string.Join(", ", expected)}]");
				Logger.LogWarning($"Got: ({manifest.GetAllAssetBundles().Length})[{string.Join(", ", manifest.GetAllAssetBundles())}]");
				var missing = expected.Except(manifest.GetAllAssetBundles());
				Logger.LogWarning($"Missing: ({missing.Count()})[{string.Join(", ", missing)}]");
			}

			return results.ToArray();
		}

		public class AssetBundleBuildResult {
			public IMod      mod;
			public string[] outputs;
		}
	}
}
#endif
