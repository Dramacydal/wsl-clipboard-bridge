namespace WslClipboardBridge;

// Minimal file logger for a tray-only app with no visible console —
// without this, diagnosing "why didn't it send" means guessing.
internal static class Logger
{
	private static readonly string LogPath = Path.Combine(
		Path.GetTempPath(), "wsl-clip-bridge.log");
	private static readonly object Lock = new();

	public static void Log(string message)
	{
		lock (Lock)
		{
			try
			{
				File.AppendAllText(LogPath,
					$"{DateTime.Now:O} {message}{Environment.NewLine}");
			}
			catch (IOException)
			{
				// Best-effort diagnostics only — never let logging
				// itself take down the clipboard handler.
			}
		}
	}
}
