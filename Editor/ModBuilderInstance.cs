using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
using Nox.CCK.Language;
using Nox.CCK.Utils;
using Nox.Editor.Panel;
using Nox.GameBuilder.Pipeline;
using Nox.ModLoader;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using IPanel = Nox.Editor.Panel.IPanel;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.GameBuilder {
	public class ModBuilderInstance : IInstance {
		private readonly ModBuilderPanel _panel;
		private readonly IWindow          _window;

		private VisualElement _modsList;
		private TextField     _outputField;
		private EnumField     _platformEnum;
		private Button        _buildButton;
		private VisualElement _buildingContainer;
		private Label         _statusLabel;
		private ProgressBar   _progressBar;
		private VisualElement _resultContainer;
		private Label         _successLabel;
		private Label         _failedLabel;
		private Label         _detailsLabel;

		private HashSet<string>   _selectedModIds = new();
		private List<VisualElement> _modItems       = new();

		public ModBuilderInstance(ModBuilderPanel panel, IWindow window, Dictionary<string, object> data) {
			_panel  = panel;
			_window = window;
			ModBuild.OnModBuildFinished.AddListener(OnModBuildFinished);
			ModBuild.OnModBuildStarted.AddListener(OnModBuildStarted);
			ModBuild.OnModBuildProgress.AddListener(OnModBuildProgress);
		}

		public IPanel GetPanel()
			=> _panel;

		public IWindow GetWindow()
			=> _window;

		public string GetTitle()
			=> "Mod Builder";

		public void OnDestroy() {
			ModBuild.OnModBuildFinished.RemoveListener(OnModBuildFinished);
			ModBuild.OnModBuildStarted.RemoveListener(OnModBuildStarted);
			ModBuild.OnModBuildProgress.RemoveListener(OnModBuildProgress);
			_panel.Instance = null;
		}

		public VisualElement GetContent() {
			var tree = _panel.API.AssetAPI.GetAsset<VisualTreeAsset>("panels/mod_builder.uxml");
			var root = tree.CloneTree();

			_modsList         = root.Q<VisualElement>("mods-list");
			_outputField      = root.Q<TextField>("output");
			_platformEnum     = root.Q<EnumField>("platform");
			_buildButton      = root.Q<Button>("build");
			_buildingContainer = root.Q<VisualElement>("building");
			_statusLabel      = root.Q<Label>("status");
			_progressBar      = root.Q<ProgressBar>("progress");
			_resultContainer  = root.Q<VisualElement>("result");
			_successLabel     = root.Q<Label>("success");
			_failedLabel      = root.Q<Label>("failed");
			_detailsLabel     = root.Q<Label>("details");

			// Output
			_outputField.value = ModBuilderPanel.OutputFolder;
			_outputField.RegisterValueChangedCallback(evt => ModBuilderPanel.OutputFolder = evt.newValue);

			var selectOutput = root.Q<Button>("select-output");
			selectOutput.clicked += () => {
				var path = EditorUtility.OpenFolderPanel("Select Output Folder", "", "");
				if (string.IsNullOrEmpty(path)) return;
				var applicationPath = Application.dataPath;
				if (path.StartsWith(applicationPath))
					path = "Assets" + path[applicationPath.Length..];
				_outputField.SetValueWithoutNotify(path);
				ModBuilderPanel.OutputFolder = path;
			};

			var openOutput = root.Q<Button>("open-output");
			openOutput.clicked += () => {
				var path = _outputField.value;
				if (string.IsNullOrEmpty(path)) return;
				if (!Directory.Exists(path))
					Directory.CreateDirectory(path);
				EditorUtility.RevealInFinder(path);
			};

			// Select All / Deselect All
			var selectAll   = root.Q<Button>("select-all");
			var deselectAll = root.Q<Button>("deselect-all");
			selectAll.clicked   += () => SelectAllMods(true);
			deselectAll.clicked += () => SelectAllMods(false);

			// Platform
			_platformEnum.Init(PlatformExtensions.CurrentPlatform);

			// Result OK button
			var okButton = root.Q<Button>("ok");
			okButton.clicked += () => ShowResult(false);

			// Build button
			_buildButton.clicked += OnBuildClicked;

			RefreshLists();
			return root;
		}

		private void RefreshLists() {
			_modsList.Clear();
			_modItems.Clear();

			try {
				var mods = Nox.ModLoader.ModManager.Mods
					.Select(m => m.GetMetadata())
					.GroupBy(m => m.GetId())
					.Select(g => g.First())
					.OrderBy(m => m.GetName() ?? m.GetId())
					.ToList();

				var itemAsset = _panel.API.AssetAPI.GetAsset<VisualTreeAsset>("panels/mod_item.uxml");
				var persisted = (ModBuilderPanel.SelectedMods ?? "").Split(',')
					.Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToHashSet();

				foreach (var meta in mods) {
					var item = itemAsset.CloneTree();
					var id   = meta.GetId();
					item.Q<Label>("id").text = id;
					item.Q<Label>("name").text = meta.GetName() ?? id;

					if (persisted.Contains(id)) {
						_selectedModIds.Add(id);
						UpdateItemHighlight(item, id);
					}

					item.RegisterCallback<ClickEvent>(evt => {
						if (_selectedModIds.Contains(id))
							_selectedModIds.Remove(id);
						else
							_selectedModIds.Add(id);
						UpdateItemHighlight(item, id);
						SaveSelection();
					});

					_modItems.Add(item);
					_modsList.Add(item);
				}
			} catch (Exception e) {
				Logger.LogWarning($"Failed to load mods: {e.Message}");
			}
		}

		private void SaveSelection()
			=> ModBuilderPanel.SelectedMods = string.Join(",", _selectedModIds);

		private void SelectAllMods(bool select) {
			foreach (var item in _modItems) {
				var id = item.Q<Label>("id").text;
				if (select) _selectedModIds.Add(id);
				else        _selectedModIds.Remove(id);
				UpdateItemHighlight(item, id);
			}
			SaveSelection();
		}

		private void UpdateItemHighlight(VisualElement item, string id) {
			bool sel = _selectedModIds.Contains(id);
			item.style.backgroundColor = sel
				? new StyleColor(new Color(0.25f, 0.45f, 0.8f, 0.6f))
				: StyleKeyword.Null;
		}

		private void OnBuildClicked() {
			if (_selectedModIds.Count == 0) {
				EditorUtility.DisplayDialog("Mod Build", "Please select at least one mod to build.", "OK");
				return;
			}

			var data = new ModBuildData {
				ModIds     = _selectedModIds.ToArray(),
				OutputPath = _outputField.value,
				Target     = (Platform)_platformEnum.value,
			};

			ModBuild.Build(data).Forget();
		}

		private void OnModBuildStarted(ModBuildData data) {
			_buildButton.SetEnabled(false);
			ShowBuildProgress(true, "Building...");
		}

		private void OnModBuildProgress(float progress, string message) {
			_statusLabel.text = message;
			_progressBar.value = progress * 100f;
		}

		private void OnModBuildFinished(BuildResult result) {
			ShowBuildProgress(false, "");
			_buildButton.SetEnabled(true);
			RefreshLists();

			_successLabel.style.display = result.IsFailed ? DisplayStyle.None : DisplayStyle.Flex;
			_failedLabel.style.display  = result.IsFailed ? DisplayStyle.Flex : DisplayStyle.None;
			_detailsLabel.text          = result.Message;
			ShowResult(true);
		}

		private void ShowBuildProgress(bool show, string message) {
			_buildingContainer.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
			if (show) {
				_statusLabel.text = message;
				_progressBar.value = 0;
			}
		}

		private void ShowResult(bool show) {
			_resultContainer.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
		}
	}
}
