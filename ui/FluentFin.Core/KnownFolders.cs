namespace FluentFin.Core;

public class KnownFolders
{
	public string ApplicationData { get; }
	public string Updates { get; }
	public string Logs { get; }

	// fluentfin-mpv: its own folder, so this build never shares settings, saved logins or logs with an upstream FluentFin install
	public const string FolderName = "FluentFinMpv";

	public KnownFolders() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName)) { }

	public KnownFolders(string root)
	{
		ApplicationData = root;
		Updates = Path.Combine(root, "Updates");
		Logs = Path.Combine(root, "Logs");

		Directory.CreateDirectory(ApplicationData);
		Directory.CreateDirectory(Logs);
		Directory.CreateDirectory(Updates);
	}
}
