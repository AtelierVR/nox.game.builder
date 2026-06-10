using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nox.CCK.Utils;
using UnityEditor;

namespace Nox.GameBuilder.Pipeline.Utils {
	/// <summary>
	/// Utilities for working with native plugin files and their Unity metadata.
	/// </summary>
	public static class PluginUtils {
		/// <summary>
		/// Infer the target platform from a plugin's folder structure.
		/// Recognizes common folder names like "linux/", "osx/", "win64/", etc.
		/// Falls back to <paramref name="defaultPlatform"/> if no hint is found.
		/// </summary>
		public static string InferPlatform(string relativePath, string defaultPlatform) {
			var firstSegment = relativePath.Split('/')[0];
			var plat = Library.InferPlatform(firstSegment);
			return plat != Platform.None ? plat.GetPlatformName() : defaultPlatform;
		}

		/// <summary>
		/// Read all compatible platforms from a native plugin's PluginImporter metadata (.meta file).
		/// Returns null if unavailable — the caller should fall back to a heuristic.
		/// </summary>
		public static string[] ReadPlatforms(string filePath) {
			try {
				var assetPath = PathUtils.ToAssetPath(filePath);
				if (assetPath == null) return null;

				if (AssetImporter.GetAtPath(assetPath) is not PluginImporter pi || !pi.isNativePlugin)
					return null;

				var platforms = new List<string>();
				foreach (BuildTarget bt in Enum.GetValues(typeof(BuildTarget))) {
					if (bt == BuildTarget.NoTarget) continue;
					if (!pi.GetCompatibleWithPlatform(bt)) continue;
					var plat = bt.GetPlatform();
					if (plat == Platform.None) {
						Nox.CCK.Utils.Logger.LogWarning($"Native plugin compatible with unmapped BuildTarget: {bt}");
						continue;
					}
					var name = plat.GetPlatformName();
					if (!platforms.Contains(name))
						platforms.Add(name);
				}

				return platforms.Count > 0 ? platforms.ToArray() : null;
			} catch {
				return null;
			}
		}

		/// <summary>
		/// Read the first compatible platform from a native plugin (convenience).
		/// </summary>
		public static string ReadPlatform(string filePath)
			=> ReadPlatforms(filePath)?.FirstOrDefault();

		/// <summary>
		/// Read the target architecture from a native plugin's PluginImporter metadata (CPU field).
		/// Returns null if unavailable — the caller should fall back to a heuristic.
		/// </summary>
		public static string ReadArchitecture(string filePath) {
			try {
				var assetPath = PathUtils.ToAssetPath(filePath);
				if (assetPath == null) return null;

				if (AssetImporter.GetAtPath(assetPath) is not PluginImporter pi || !pi.isNativePlugin)
					return null;

				// Try each platform's CPU data — first non-empty wins
				string cpu = null;
				foreach (Platform p in Enum.GetValues(typeof(Platform))) {
					if (p == Platform.None) continue;
					var bt = p.GetBuildTarget();
					if (bt == BuildTarget.NoTarget) continue;
					cpu = pi.GetPlatformData(bt, "CPU");
					if (!string.IsNullOrEmpty(cpu)) break;
				}
				return !string.IsNullOrEmpty(cpu) ? cpu.ToLowerInvariant() : null;
			} catch {
				return null;
			}
		}

		/// <summary>
		/// Infer the target architecture from a plugin's folder name
		/// (e.g. "win32" → "x86", "win64" → "x86_64", "arm64" → "arm64").
		/// </summary>
		public static string InferArchitecture(string relativePath) {
			var segments = relativePath.Split('/');
			if (segments.Length < 2) return null;
			var arch = Library.InferArchitecture(segments[^2]);
			return arch != Architecture.None ? arch.GetArchitectureName() : null;
		}
	}
}