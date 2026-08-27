namespace WslClipboardBridge;

internal sealed class BridgeSettings
{
	// Null/empty means `wsl.exe -e ...` targets the default distro,
	// which covers the common single-distro setup this tool is built
	// for. Override with --distro for a multi-distro machine.
	public string? Distro { get; init; }

	public static BridgeSettings FromArgs(string[] args)
	{
		string? distro = null;
		for (int i = 0; i < args.Length; i++)
		{
			if (args[i] == "--distro" && i + 1 < args.Length)
			{
				distro = args[++i];
			}
		}

		return new BridgeSettings { Distro = distro };
	}
}
