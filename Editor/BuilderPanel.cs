using System;
using System.Collections.Generic;
using Nox.CCK.Mods.Cores;
using Nox.CCK.Mods.Initializers;
using Nox.CCK.Utils;
using Nox.Editor.Panel;

namespace Nox.GameBuilder {
	public class BuilderPanel : IEditorModInitializer, IPanel {
		internal IEditorModCoreAPI API;

		private static readonly string[] PanelPath = {
			"game",
			"builder"
		};

		public void OnInitializeEditor(IEditorModCoreAPI api)
			=> API = api;

		public void OnDisposeEditor()
			=> API = null;

		public string[] GetPath()
			=> PanelPath;

		public BuilderInstance Instance;

		public IInstance[] GetInstances()
			=> Instance != null
				? new IInstance[] { Instance }
				: Array.Empty<IInstance>();

		public string GetLabel()
			=> "Game/Builder";

		public static string OutputFolder {
			get => Config.LoadEditor().Get("game.builder.output_folder", "Build");
			set {
				var config = Config.LoadEditor();
				config.Set("game.builder.output_folder", value);
				config.Save();
			}
		}

		public static bool OptDevelopment {
			get => Config.LoadEditor().Get("game.builder.opt.development", false);
			set {
				var config = Config.LoadEditor();
				config.Set("game.builder.opt.development", value);
				config.Save();
			}
		}

		public static bool OptAllowDebugging {
			get => Config.LoadEditor().Get("game.builder.opt.allow_debugging", false);
			set {
				var config = Config.LoadEditor();
				config.Set("game.builder.opt.allow_debugging", value);
				config.Save();
			}
		}

		public static bool OptProfiler {
			get => Config.LoadEditor().Get("game.builder.opt.profiler", false);
			set {
				var config = Config.LoadEditor();
				config.Set("game.builder.opt.profiler", value);
				config.Save();
			}
		}

		public static bool OptScriptsOnly {
			get => Config.LoadEditor().Get("game.builder.opt.scripts_only", false);
			set {
				var config = Config.LoadEditor();
				config.Set("game.builder.opt.scripts_only", value);
				config.Save();
			}
		}

		public static bool OptDeepProfiling {
			get => Config.LoadEditor().Get("game.builder.opt.deep_profiling", false);
			set {
				var config = Config.LoadEditor();
				config.Set("game.builder.opt.deep_profiling", value);
				config.Save();
			}
		}

		public IInstance Instantiate(IWindow window, Dictionary<string, object> data) {
			if (Instance != null)
				throw new InvalidOperationException($"{nameof(BuilderPanel)} only supports a single instance.");
			return Instance = new BuilderInstance(this, window, data);
		}
	}
}