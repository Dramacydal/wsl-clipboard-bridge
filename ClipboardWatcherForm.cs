using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace WslClipboardBridge;

// Never shown — exists only to own a native window handle so
// AddClipboardFormatListener has something to register and WndProc has
// somewhere to receive WM_CLIPBOARDUPDATE. NotifyIcon alone has no
// window of its own to hook.
internal sealed class ClipboardWatcherForm : Form
{
	private readonly WslInvoker _wsl;
	private byte[]? _lastSentHash;

	// Toggled from the tray menu. Named distinctly from Control.Enabled
	// (which this Form still has, inherited, and which is unrelated —
	// this hidden window's own enabled/visible state never changes).
	[System.ComponentModel.DesignerSerializationVisibility(
		System.ComponentModel.DesignerSerializationVisibility.Hidden)]
	public bool WatchingEnabled { get; set; } = true;

	[System.ComponentModel.DesignerSerializationVisibility(
		System.ComponentModel.DesignerSerializationVisibility.Hidden)]
	public ClipboardMode Mode
	{
		get => _wsl.Mode;
		set => _wsl.Mode = value;
	}

	public event Action<string>? StatusChanged;

	public ClipboardWatcherForm(BridgeSettings settings)
	{
		_wsl = new WslInvoker(settings.Distro);
		ShowInTaskbar = false;
		WindowState = FormWindowState.Minimized;
		FormBorderStyle = FormBorderStyle.FixedToolWindow;
	}

	// Force handle creation without ever making the form visible.
	protected override void SetVisibleCore(bool value)
		=> base.SetVisibleCore(false);

	protected override void OnHandleCreated(EventArgs e)
	{
		base.OnHandleCreated(e);
		NativeMethods.AddClipboardFormatListener(Handle);
	}

	protected override void OnHandleDestroyed(EventArgs e)
	{
		NativeMethods.RemoveClipboardFormatListener(Handle);
		base.OnHandleDestroyed(e);
	}

	protected override void WndProc(ref Message m)
	{
		if (m.Msg == NativeMethods.WM_CLIPBOARDUPDATE)
		{
			Logger.Log("WM_CLIPBOARDUPDATE received");
			// Fire-and-forget: WndProc must not block the message pump,
			// and a slow `wsl.exe` invocation must not stall the next
			// clipboard event.
			_ = HandleClipboardUpdateAsync();
		}

		base.WndProc(ref m);
	}

	private async Task HandleClipboardUpdateAsync()
	{
		try
		{
			await HandleClipboardUpdateCoreAsync();
		}
		catch (Exception ex)
		{
			// This runs fire-and-forget from WndProc — an unobserved
			// exception here would just vanish silently instead of
			// crashing anything, which is worse for diagnosis.
			Logger.Log($"Unhandled exception: {ex}");
		}
	}

	private async Task HandleClipboardUpdateCoreAsync()
	{
		if (!WatchingEnabled)
		{
			Logger.Log("Disabled via tray menu, ignoring");
			return;
		}

		ClipboardPayload? payload = TryReadClipboardPayload();
		if (payload is null)
		{
			Logger.Log("Nothing pastable on clipboard");
			return;
		}

		byte[] hash = payload switch
		{
			ImagePayload img => SHA256.HashData(img.PngBytes),
			FileReferencePayload files => SHA256.HashData(
				Encoding.UTF8.GetBytes(string.Join('\n', files.WindowsPaths))),
			_ => throw new InvalidOperationException("Unknown payload type"),
		};

		if (_lastSentHash is not null && hash.AsSpan().SequenceEqual(_lastSentHash))
		{
			// Same content re-announced (some apps add clipboard formats
			// in several steps, firing WM_CLIPBOARDUPDATE more than once
			// per copy) — nothing changed, skip the round trip.
			Logger.Log("Duplicate clipboard content, skipping");
			return;
		}

		// Set before the first await, not after success: real copy
		// actions reliably fire WM_CLIPBOARDUPDATE twice a few hundred
		// ms apart for the same content (observed empirically), which
		// lands well inside the ~1s a wsl.exe round trip takes. Without
		// this, the second event races the first — two concurrent
		// `xclip`/`wl-copy` processes fighting over the same X11/Wayland
		// selection, which was silently losing the content entirely.
		_lastSentHash = hash;

		(bool ok, string log, string statusNoun) = payload switch
		{
			ImagePayload img => await SendImageAsync(img),
			FileReferencePayload file => await SendFileReferenceAsync(file),
			_ => throw new InvalidOperationException("Unknown payload type"),
		};
		Logger.Log($"wsl.exe result: {log}");

		if (ok)
		{
			StatusChanged?.Invoke(
				$"Set WSL clipboard: {statusNoun} at {DateTime.Now:T}");
		}
		else
		{
			// Allow a genuine retry: a future copy of this exact same
			// content (e.g. the user retries the same screenshot) should
			// not be deduped away forever because of one failed attempt.
			_lastSentHash = null;
			StatusChanged?.Invoke(
				$"Failed to set WSL clipboard at {DateTime.Now:T} — see log");
		}
	}

	private async Task<(bool ok, string log, string statusNoun)> SendImageAsync(
		ImagePayload img)
	{
		// Unique per event: xclip/wl-copy read this file in the
		// background (see WslInvoker), so a fixed name would let a
		// second copy overwrite it mid-read on back-to-back copies.
		string tempPath = Path.Combine(Path.GetTempPath(),
			$"wsl-clip-bridge-{Guid.NewGuid():N}.png");
		Logger.Log($"New image detected: {img.PngBytes.Length} bytes, writing to {tempPath}");
		await File.WriteAllBytesAsync(tempPath, img.PngBytes);
		CleanUpOldTempFiles(tempPath);

		(bool ok, string log) = await _wsl.SetClipboardImageAsync(
			tempPath, CancellationToken.None);
		return (ok, log, $"image ({img.PngBytes.Length:N0} bytes)");
	}

	// text/uri-list points at each file's own WSL-mounted path — nothing
	// is copied or re-encoded, unlike the image path. The format is one
	// URI per line, CRLF-terminated (RFC 2483) — a single-entry list is
	// just the N=1 case of the same thing. Whether any given Linux app
	// reacts to Ctrl+V with this MIME type, and whether it treats a
	// multi-entry list as multiple attachments, is up to that app (see
	// BuildFileUri).
	private async Task<(bool ok, string log, string statusNoun)> SendFileReferenceAsync(
		FileReferencePayload files)
	{
		string[] uris = files.WindowsPaths.Select(_wsl.BuildFileUri).ToArray();
		string tempPath = Path.Combine(Path.GetTempPath(),
			$"wsl-clip-bridge-{Guid.NewGuid():N}.uri-list");
		Logger.Log("New file reference(s) detected: "
			+ string.Join(", ", files.WindowsPaths.Zip(uris,
				(path, uri) => $"{path} -> {uri}")));
		await File.WriteAllTextAsync(tempPath,
			string.Join("\r\n", uris) + "\r\n");
		CleanUpOldTempFiles(tempPath);

		(bool ok, string log) = await _wsl.SetClipboardContentAsync(
			tempPath, "text/uri-list", CancellationToken.None);
		string statusNoun = files.WindowsPaths.Count == 1
			? $"file ({Path.GetFileName(files.WindowsPaths[0])})"
			: $"{files.WindowsPaths.Count} files";
		return (ok, log, statusNoun);
	}

	// Best-effort: delete previous run's temp files once they're old
	// enough that no backgrounded xclip/wl-copy could still be reading
	// them (the wsl.exe script itself waits 0.3s before returning).
	// Never touches `keep`, the file just written for this event.
	private static void CleanUpOldTempFiles(string keep)
	{
		try
		{
			string dir = Path.GetTempPath();
			foreach (string path in Directory.EnumerateFiles(
				dir, "wsl-clip-bridge-*"))
			{
				if (path == keep)
				{
					continue;
				}
				if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path)
					> TimeSpan.FromSeconds(30))
				{
					File.Delete(path);
				}
			}
		}
		catch (IOException)
		{
			// Cleanup is best-effort; a locked/already-gone file is fine.
		}
	}

	// Clipboard.GetImage() decodes CF_DIB/CF_BITMAP via GDI+, which
	// handles the BI_BITFIELDS variant WSLg re-exports fine — the format
	// only trips up the lighter decoders (libvips/sharp) on the Linux
	// side, which is the whole reason this bridge exists. That decode
	// path only applies to raw clipboard image data (screenshots,
	// "Copy image" in a browser) — there's no file to reference there.
	//
	// Ctrl+C on a file in Explorer puts a file reference (CF_HDROP) on
	// the clipboard instead, not bitmap data. Every such file — image
	// or not — goes through as a plain file reference: no decode, no
	// re-encode, the Linux side gets the original bytes untouched.
	private static ClipboardPayload? TryReadClipboardPayload()
	{
		// The clipboard is a shared OS resource; another process can
		// legitimately hold it open for a few milliseconds. Retry
		// briefly instead of dropping the copy.
		for (int attempt = 0; attempt < 5; attempt++)
		{
			try
			{
				if (Clipboard.ContainsImage())
				{
					using Image? image = Clipboard.GetImage();
					return image is null ? null : new ImagePayload(EncodeAsPng(image));
				}

				if (Clipboard.ContainsFileDropList())
				{
					var files = new List<string>();
					foreach (string? file in Clipboard.GetFileDropList())
					{
						if (file is not null)
						{
							files.Add(file);
						}
					}

					return files.Count == 0 ? null : new FileReferencePayload(files);
				}

				return null;
			}
			catch (ExternalException)
			{
				Thread.Sleep(50);
			}
		}

		return null;
	}

	private static byte[] EncodeAsPng(Image image)
	{
		using var ms = new MemoryStream();
		image.Save(ms, ImageFormat.Png);
		return ms.ToArray();
	}
}

internal abstract record ClipboardPayload;

internal sealed record ImagePayload(byte[] PngBytes) : ClipboardPayload;

internal sealed record FileReferencePayload(IReadOnlyList<string> WindowsPaths)
	: ClipboardPayload;
