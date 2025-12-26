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

		public IInstance Instantiate(IWindow window, Dictionary<string, object> data) {
			if (Instance != null)
				throw new InvalidOperationException($"{nameof(BuilderPanel)} only supports a single instance.");
			return Instance = new BuilderInstance(this, window, data);
		}
	}
}