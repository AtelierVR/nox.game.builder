using System;
using System.Collections.Generic;
using Nox.CCK.Mods.Cores;
using Nox.CCK.Mods.Initializers;
using Nox.CCK.Utils;
using Nox.Editor.Panel;

namespace Nox.GameBuilder {
	public class ModBuilderPanel : IEditorModInitializer, IPanel {
		internal IEditorModCoreAPI API;

		private static readonly string[] PanelPath = {
			"mod",
			"builder"
		};

		public void OnInitializeEditor(IEditorModCoreAPI api)
			=> API = api;

		public void OnDisposeEditor()
			=> API = null;

		public string[] GetPath()
			=> PanelPath;

		public ModBuilderInstance Instance;

		public IInstance[] GetInstances()
			=> Instance != null
				? new IInstance[] { Instance }
				: Array.Empty<IInstance>();

		public string GetLabel()
			=> "Mod/Builder";

		public static string OutputFolder {
			get => Config.LoadEditor().Get("mod.builder.output_folder", "Build/Mods");
			set {
				var config = Config.LoadEditor();
				config.Set("mod.builder.output_folder", value);
				config.Save();
			}
		}

		public static string SelectedMods {
			get => Config.LoadEditor().Get("mod.builder.selected_mods", "");
			set {
				var config = Config.LoadEditor();
				config.Set("mod.builder.selected_mods", value);
				config.Save();
			}
		}

		public IInstance Instantiate(IWindow window, Dictionary<string, object> data) {
			if (Instance != null)
				throw new InvalidOperationException($"{nameof(ModBuilderPanel)} only supports a single instance.");
			return Instance = new ModBuilderInstance(this, window, data);
		}
	}
}
