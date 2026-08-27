namespace WslClipboardBridge;

// Persists the tray menu's Mode selection across restarts. Deliberately
// a single plain-text file, not a settings framework — one enum value.
internal static class ModeStore
{
	// Next to the exe rather than %LOCALAPPDATA% — for a single-file
	// publish, AppContext.BaseDirectory is the directory containing the
	// actual running exe, not a temp extraction path.
	private static readonly string FilePath = Path.Combine(
		AppContext.BaseDirectory, "mode.txt");

	public static ClipboardMode Load()
	{
		try
		{
			string text = File.ReadAllText(FilePath).Trim();
			if (Enum.TryParse(text, ignoreCase: true, out ClipboardMode mode))
			{
				return mode;
			}
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}

		return ClipboardMode.X11;
	}

	public static void Save(ClipboardMode mode)
	{
		try
		{
			File.WriteAllText(FilePath, mode.ToString());
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}
}
