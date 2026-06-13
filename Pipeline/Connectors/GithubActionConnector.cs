using System;
using System.IO;
using System.Text;
using Nox.CCK.Attributes;
using Nox.CCK.Utils;
using UnityEngine;

namespace Nox.GameBuilder.Pipeline {
	/// <summary>
	/// Auto-detects --githubAction and hooks into the build pipeline via [Nox]
	/// attributes to emit GitHub Actions workflow commands (::group, ::warning,
	/// ::error, ::notice, step summary). Fully self-contained — no external
	/// setup needed.
	/// </summary>
	public static class GithubActionConnector {
		private const string SummaryEnv = "GITHUB_STEP_SUMMARY";

		/// <summary>True when --githubAction was passed on the command line.</summary>
		public static readonly bool IsGithubAction;

		/// <summary>Original log handler to restore on disable.</summary>
		private static ILogHandler _originalHandler;

		/// <summary>StringBuilder accumulating step summary content.</summary>
		private static readonly StringBuilder SummaryBuffer = new();

		/// <summary>Current nesting depth for groups.</summary>
		private static int _groupDepth;

		// ═══════════════════════════════════════════════════════════════
		// Static init — auto-detect --githubAction
		// ═══════════════════════════════════════════════════════════════

		static GithubActionConnector() {
			try {
				IsGithubAction = ArgsParser.Parse().GetBool("githubAction");
			} catch {
				IsGithubAction = false;
			}

			if (!IsGithubAction) return;

			_originalHandler = Debug.unityLogger.logHandler;
			Debug.unityLogger.logHandler = new GithubActionLogHandler(_originalHandler);

			AppendSummary($"# Nox Build Summary — {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC\n");
		}

		// ═══════════════════════════════════════════════════════════════
		// [Nox] event hooks — triggered automatically by the build pipeline
		// ═══════════════════════════════════════════════════════════════

		[NoxInvokable("build:mod:start")]
		public static void OnBuildStart() {
			if (!IsGithubAction) return;
			Group("Nox Mod Build");
			SummaryLine("## Build Started");
			SummaryLine("");
		}

		[NoxInvokable("build:mod:prepare")]
		public static void OnBuildPrepare() {
			if (!IsGithubAction) return;
			Group("Preparing output");
		}

		[NoxInvokable("build:mod:player:done")]
		public static void OnPlayerBuildDone() {
			if (!IsGithubAction) return;
			EndGroup();
			Group("Processing mods");
		}

		[NoxInvokable("build:mod:platform:start")]
		public static void OnPlatformStart() {
			if (!IsGithubAction) return;
		}

		[NoxInvokable("build:mod:platform:done")]
		public static void OnPlatformDone() {
			if (!IsGithubAction) return;
			FlushSummary();
		}

		[NoxInvokable("build:mod:mod:done")]
		public static void OnModDone(string modId, int dllCount, int bundleCount) {
			if (!IsGithubAction) return;
			SummaryLine($"- **{modId}**: {dllCount} DLLs, {bundleCount} bundles");
			Notice($"Mod built: {modId} ({dllCount} DLLs, {bundleCount} bundles)");
		}

		[NoxInvokable("build:mod:done")]
		public static void OnBuildDone() {
			if (!IsGithubAction) return;
			while (_groupDepth > 0)
				EndGroup();
			SummaryLine("");
			SummaryLine($"---");
			SummaryLine($"*Build completed at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC*");
			FlushSummary();
		}

		// ═══════════════════════════════════════════════════════════════
		// Internal helpers
		// ═══════════════════════════════════════════════════════════════

		private static void Group(string title) {
			Console.WriteLine($"::group::{Escape(title)}");
			_groupDepth++;
		}

		private static void EndGroup() {
			if (_groupDepth <= 0) return;
			Console.WriteLine("::endgroup::");
			_groupDepth--;
		}

		private static void Notice(string message) {
			Console.WriteLine($"::notice::{Escape(message)}");
		}

		private static void AppendSummary(string markdown) {
			SummaryBuffer.AppendLine(markdown);
		}

		private static void SummaryLine(string format, params object[] args) {
			AppendSummary(string.Format(format, args));
		}

		private static void FlushSummary() {
			var path = Environment.GetEnvironmentVariable(SummaryEnv);
			if (string.IsNullOrEmpty(path)) return;
			try {
				File.AppendAllText(path, SummaryBuffer.ToString());
				SummaryBuffer.Clear();
			} catch {
				// Best-effort
			}
		}

		private static string Escape(string message) {
			if (string.IsNullOrEmpty(message)) return message;
			return message
				.Replace("%", "%25")
				.Replace("\r", "%0D")
				.Replace("\n", "%0A");
		}

		// ═══════════════════════════════════════════════════════════════
		// Log handler — auto-converts Unity logs → GH Actions commands
		// ═══════════════════════════════════════════════════════════════

		private class GithubActionLogHandler : ILogHandler {
			private readonly ILogHandler _inner;

			public GithubActionLogHandler(ILogHandler inner) {
				_inner = inner;
			}

			public void LogFormat(UnityEngine.LogType logType, UnityEngine.Object context, string format, params object[] args) {
				var message = string.Format(format, args);
				LogMessage(logType, context, message);
			}

			public void LogException(Exception exception, UnityEngine.Object context) {
				var msg = exception?.ToString() ?? "Unknown exception";
				Console.WriteLine($"::error::{Escape(msg)}");
				_inner?.LogException(exception, context);
			}

			private void LogMessage(UnityEngine.LogType logType, UnityEngine.Object context, string message) {
				switch (logType) {
					case UnityEngine.LogType.Error:
					case UnityEngine.LogType.Exception:
						Console.WriteLine($"::error::{Escape(message)}");
						break;
					case UnityEngine.LogType.Warning:
						Console.WriteLine($"::warning::{Escape(message)}");
						break;
					case UnityEngine.LogType.Assert:
						Console.WriteLine($"::error title=Assert::{Escape(message)}");
						break;
					default:
						Console.WriteLine(message);
						break;
				}

				_inner?.LogFormat(logType, context, "{0}", message);
			}
		}
	}
}


