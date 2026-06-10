using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Nox.GameBuilder.Pipeline.Utils {
	/// <summary>
	/// Path utilities for converting between file-system and Unity asset paths.
	/// </summary>
	public static class PathUtils {
		/// <summary>
		/// Normalize a path to use forward slashes and resolve relative segments.
		/// </summary>
		public static string Normalize(string path)
			=> Path.GetFullPath(path).Replace('\\', '/');

		/// <summary>
		/// Convert a full file-system path to a Unity asset path (e.g. "Packages/..." or "Assets/...").
		/// Returns null if the path is outside the project.
		/// </summary>
		public static string ToAssetPath(string filePath) {
			filePath = Normalize(filePath);

			var assetsPath = Normalize(Application.dataPath);
			if (filePath.StartsWith(assetsPath))
				return "Assets" + filePath.Substring(assetsPath.Length);

			var packagesPath = Normalize(Path.Combine(Application.dataPath, "..", "Packages"));
			if (filePath.StartsWith(packagesPath))
				return "Packages" + filePath.Substring(packagesPath.Length);

			return null;
		}
	}
}
