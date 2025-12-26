namespace Nox.GameBuilder.Pipeline {
	public class BuildResult {
		public BuildResultType Type;
		public string          Message;
		public string          Output;

		public bool IsFailed
			=> Type.HasFlag(BuildResultType.Failed);
	}
}
