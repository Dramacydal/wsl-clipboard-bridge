using System.Runtime.InteropServices;

namespace WslClipboardBridge;

internal static class NativeMethods
{
	public const int WM_CLIPBOARDUPDATE = 0x031D;

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool AddClipboardFormatListener(IntPtr hwnd);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
