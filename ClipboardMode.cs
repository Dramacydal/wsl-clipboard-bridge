namespace WslClipboardBridge;

// Which Linux-side clipboard protocol should end up holding the real
// image bytes. xclip and wl-copy can't both hold content at once here
// (see WslInvoker) — whichever backs the target app's actual rendering
// path (XWayland vs native Wayland) needs to run last.
internal enum ClipboardMode
{
	X11,
	Wayland,
}
