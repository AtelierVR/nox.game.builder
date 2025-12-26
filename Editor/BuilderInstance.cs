using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
using Nox.CCK.Language;
using Nox.CCK.Utils;
using Nox.Editor.Panel;
using Nox.GameBuilder.Pipeline;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using IPanel = Nox.Editor.Panel.IPanel;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.GameBuilder {
	public class BuilderInstance : IInstance {
		private readonly BuilderPanel _panel;
		private readonly IWindow      _window;

		public BuilderInstance(BuilderPanel panel, IWindow window, Dictionary<string, object> data) {
			_panel  = panel;
			_window = window;
			Builder.OnBuildFinished.AddListener(OnBuildFinished);
			Builder.OnBuildStarted.AddListener(OnBuildStarted);
			Builder.OnBuildProgress.AddListener(OnBuildProgress);
		}


		public IPanel GetPanel()
			=> _panel;

		public IWindow GetWindow()
			=> _window;

		public string GetTitle()
			=> "Game Builder";

		public void OnDestroy() {
			Builder.OnBuildFinished.RemoveListener(OnBuildFinished);
			Builder.OnBuildStarted.RemoveListener(OnBuildStarted);
			Builder.OnBuildProgress.RemoveListener(OnBuildProgress);
			_panel.Instance = null;
		}


		private void OnOpenOutputClicked(ClickEvent evt) {
			if (string.IsNullOrEmpty(_outputField.value)) return;
			if (!Directory.Exists(_outputField.value))
				Directory.CreateDirectory(_outputField.value);
			Application.OpenURL(_outputField.value);
		}

		private void OnSelectOutputClicked(ClickEvent evt) {
			var path = EditorUtility.OpenFolderPanel("Select Output Folder", "", "");
			if (string.IsNullOrEmpty(path)) return;
			var applicationPath = Application.dataPath;
			if (path.StartsWith(applicationPath))
				path = "Assets" + path[applicationPath.Length..];
			_outputField.SetValueWithoutNotify(path);
			BuilderPanel.OutputFolder = path;
		}

		private static void OnPlatformChanged(ChangeEvent<Enum> evt) {
			/*
			var mod = ModDescriptorHelper.CurrentMod;
			if (!mod) return;
			mod.target = (Platform)evt.newValue;
			EditorUtility.SetDirty(mod);
			*/
		}

		private void OnBuildClicked(ClickEvent evt) {
			var data = new BuildData {
				OutputPath = BuilderPanel.OutputFolder,
				Mods       = Builder.GetKernelMods(_panel.API.ModAPI.GetMods()),
				Target     = (Platform)_platformEnum.value
			};

			Builder.Build(data).Forget();
		}

		private void OnBuildStarted() {
			_buildButton.SetEnabled(false);
			ShowBuildProgress(0f, LanguageManager.Get("game.builder.status.starting"));
		}


		private void ShowBuildProgress(float progress, string status) {
			_buildingContainer.style.display = DisplayStyle.Flex;
			_buildingProgressBar.value       = progress * 100f;
			_buildingStatusLabel.text        = status;
			Logger.ShowProgress("Building Game", status, progress);
		}

		private void HideBuildProgress() {
			_buildingContainer.style.display = DisplayStyle.None;
			Logger.ClearProgress();
		}

		private void OnBuildStarted(BuildData arg0)
			=> OnBuildStarted();

		private void OnBuildFinished(BuildResult arg0) {
			HideBuildProgress();
			_resultContainer.style.display    = DisplayStyle.Flex;
			_resultFailedLabel.style.display  = arg0.IsFailed ? DisplayStyle.Flex : DisplayStyle.None;
			_resultSuccessLabel.style.display = arg0.IsFailed ? DisplayStyle.None : DisplayStyle.Flex;
			_resultDetailsLabel.text = !arg0.IsFailed
				? LanguageManager.Get("game.builder.result.success", new object[] { arg0.Output })
				: arg0.Message;
		}

		private void OnBuildProgress(float progress, string status) {
			_buildingProgressBar.value = progress * 100;
			_buildingStatusLabel.text  = status;
			ShowBuildProgress(progress, status);
		}

		private void OnBuildResultOKClicked(ClickEvent evt) {
			_resultContainer.style.display = DisplayStyle.None;
			// var mod = ModDescriptorHelper.CurrentMod;
			// _buildButton.SetEnabled(mod && ModNotificationHelper.Allowed);
			_buildButton.SetEnabled(true);
		}

		private VisualElement _modsList;
		private VisualElement _scenesList;
		private TextField     _outputField;
		private Button        _openOutputButton;
		private Button        _buildButton;
		private Button        _selectOutputButton;
		private EnumField     _platformEnum;

		private VisualElement _buildingContainer;
		private Label         _buildingStatusLabel;
		private ProgressBar   _buildingProgressBar;

		private VisualElement _resultContainer;
		private Label         _resultFailedLabel;
		private Label         _resultSuccessLabel;
		private Button        _resultOkButton;
		private Label         _resultDetailsLabel;

		private VisualElement _content;

		public VisualElement GetContent() {
			if (_content != null)
				return _content;

			var root = _panel.API.AssetAPI
				.GetAsset<VisualTreeAsset>("panels/builder.uxml")
				.CloneTree();
			root.style.flexGrow  = 1;
			root.style.minHeight = 0;

			_modsList           = root.Q<VisualElement>("mods-list");
			_scenesList         = root.Q<VisualElement>("scenes-list");
			_outputField        = root.Q<TextField>("output");
			_openOutputButton   = root.Q<Button>("open-output");
			_buildButton        = root.Q<Button>("build");
			_selectOutputButton = root.Q<Button>("select-output");
			_platformEnum       = root.Q<EnumField>("platform");

			_buildingContainer   = root.Q<VisualElement>("building");
			_buildingStatusLabel = _buildingContainer.Q<Label>("status");
			_buildingProgressBar = _buildingContainer.Q<ProgressBar>("progress");

			_resultContainer    = root.Q<VisualElement>("result");
			_resultFailedLabel  = _resultContainer.Q<Label>("failed");
			_resultSuccessLabel = _resultContainer.Q<Label>("success");
			_resultOkButton     = _resultContainer.Q<Button>("ok");
			_resultDetailsLabel = _resultContainer.Q<Label>("details");

			_openOutputButton.RegisterCallback<ClickEvent>(OnOpenOutputClicked);
			_selectOutputButton.RegisterCallback<ClickEvent>(OnSelectOutputClicked);
			_buildButton.RegisterCallback<ClickEvent>(OnBuildClicked);
			_outputField.SetValueWithoutNotify(BuilderPanel.OutputFolder);
			_platformEnum.RegisterCallback<ChangeEvent<Enum>>(OnPlatformChanged);
			_platformEnum.Init(PlatformExtensions.CurrentPlatform);
			_resultOkButton.RegisterCallback<ClickEvent>(OnBuildResultOKClicked);

			_buildingContainer.style.display = DisplayStyle.None;
			_resultContainer.style.display   = DisplayStyle.None;

			RefreshLists();
			if (Builder.IsBuilding) OnBuildStarted();

			return _content = root;
		}

		private void RefreshLists() {
			_modsList.Clear();
			var mods         = Builder.GetKernelMods(_panel.API.ModAPI.GetMods());
			var itemTemplate = _panel.API.AssetAPI.GetAsset<VisualTreeAsset>("panels/mod_item.uxml");

			foreach (var mod in mods) {
				var meta      = mod.GetMetadata();
				var container = itemTemplate.CloneTree();

				var iconPath = meta.GetIcon();
				var icon     = !string.IsNullOrEmpty(iconPath) ? AssetDatabase.LoadAssetAtPath<Texture2D>(iconPath) : null;

				var image = container.Q<Image>("icon");
				image.image = icon;
				if (icon != null) image.style.backgroundColor = StyleKeyword.Null;

				container.Q<Label>("name").text = meta.GetName();
				container.Q<Label>("id").text   = meta.GetId();
				_modsList.Add(container);
			}

			_scenesList.Clear();
			var scenes        = Builder.GetScenesToBuild(mods);
			var sceneTemplate = _panel.API.AssetAPI.GetAsset<VisualTreeAsset>("panels/scene_item.uxml");

			foreach (var scene in scenes) {
				var container = sceneTemplate.CloneTree();
				var asset     = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(scene);
				var icon = AssetPreview.GetAssetPreview(asset)
					?? AssetPreview.GetMiniThumbnail(asset);

				var image = container.Q<Image>("icon");
				image.image = icon;
				if (icon != null) image.style.backgroundColor = StyleKeyword.Null;

				container.Q<Label>("name").text = Path.GetFileNameWithoutExtension(scene);
				container.Q<Label>("path").text = scene;
				_scenesList.Add(container);
			}
		}
	}
}