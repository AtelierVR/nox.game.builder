namespace Nox.GameBuilder.Pipeline {
	[System.Flags]
	public enum BuildResultType {
		Success,
		AlreadyBuilding,
		EditorCompiling,
		EditorPlaying,
		UnsupportedTarget,
		InvalidTarget,
		Failed
	}
}
