namespace WslClipboardBridge;

internal static class Program
{
	[STAThread]
	private static void Main(string[] args)
	{
		ApplicationConfiguration.Initialize();
		BridgeSettings settings = BridgeSettings.FromArgs(args);
		Application.Run(new TrayContext(settings));
	}
}
