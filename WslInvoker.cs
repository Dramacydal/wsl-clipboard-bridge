using System.Diagnostics;

namespace WslClipboardBridge;

// Replaces an earlier TCP-daemon design: no persistent Linux-side
// process, no network, no systemd unit. Just write the PNG to a temp
// file and shell out to `wsl.exe` once per copy. Simpler, and it
// sidesteps the WSL2 loopback-NAT race that could truncate a payload
// sent right before the socket closed.
internal sealed class WslInvoker
{
	// WSLg only exports DISPLAY/WAYLAND_DISPLAY/XDG_RUNTIME_DIR into
	// interactive shell sessions (via a profile script), not into a
	// fresh `wsl.exe -e` invocation or a systemd unit — confirmed by
	// testing both. The paths themselves are fixed by WSLg regardless
	// of session, so hardcoding is the only option either way.
	private const string DisplayEnv = "DISPLAY=:0";
	private const string WaylandEnv = "WAYLAND_DISPLAY=wayland-0";
	private const string RuntimeDirEnv = "XDG_RUNTIME_DIR=/mnt/wslg/runtime-dir";

	private readonly string? _distro;

	// Toggled from the tray menu's Mode submenu. Not a Form/Control
	// property, so no WFO1000 designer-serialization concern here.
	public ClipboardMode Mode { get; set; } = ClipboardMode.X11;

	public WslInvoker(string? distro)
	{
		_distro = distro;
	}

	// Both tools work by forking a process that stays alive to answer
	// future paste requests — X11/Wayland selections have no
	// owner-independent storage. `wsl.exe -e` tears down the whole
	// session (and everything in it, including that fork) the moment
	// this script exits, so each is launched via `setsid ... & disown`
	// to give it its own session, detached from the one `wsl.exe` is
	// about to kill. Confirmed empirically: without setsid, xclip/wl-copy
	// vanish the instant wsl.exe returns.
	//
	// xclip and wl-copy CANNOT both hold the clipboard at once here:
	// WSLg runs an XWayland<->Wayland clipboard proxy that reacts to
	// either side changing by seizing ownership on the other side too —
	// but for image formats it only seizes, it doesn't actually carry
	// the bytes across. Confirmed empirically: starting wl-copy kills a
	// running xclip (SelectionClear) and vice versa, whichever ran most
	// recently "wins" as the sole owner with real content; the other
	// side ends up owned-but-empty. So they run sequentially, never
	// backgrounded together, and whichever protocol `Mode` targets goes
	// last so it's the one left holding the real image.
	//
	// WSLg's own Windows->Linux sync reacts to the same copy event we
	// do and can clobber our image with its own broken image/bmp
	// translation moments after ours lands (matches a community report
	// of the same race: github.com/rajveerb/wsl-clip-bridge). Guard
	// against it by checking the target side's own clipboard state
	// afterward and re-asserting once if it lost.
	// __MIME__ is filled in per call — image/png for pasted images,
	// text/uri-list for a plain file reference (see BuildFileUri). Both
	// go through the same xclip/wl-copy dance since the ownership
	// conflict and WSLg re-sync race apply regardless of MIME type.
	private const string ScriptTemplate = """
		xclip_log=/tmp/wsl-clip-bridge-xclip.log
		wlcopy_log=/tmp/wsl-clip-bridge-wlcopy.log

		set_wlcopy() {
			: > "$wlcopy_log"
			if command -v wl-copy >/dev/null; then
				setsid wl-copy --type '__MIME__' <"$0" \
					>"$wlcopy_log" 2>&1 &
				disown
			fi
		}
		set_xclip() {
			: > "$xclip_log"
			if command -v xclip >/dev/null; then
				setsid xclip -selection clipboard -t '__MIME__' -i "$0" \
					</dev/null >"$xclip_log" 2>&1 &
				disown
			fi
		}
		xclip_has_target() {
			command -v xclip >/dev/null \
				&& xclip -selection clipboard -t TARGETS -o 2>/dev/null \
					| grep -qx '__MIME__'
		}
		wlcopy_has_target() {
			command -v wl-paste >/dev/null \
				&& wl-paste --list-types 2>/dev/null \
					| grep -qx '__MIME__'
		}

		if [ "__MODE__" = wayland ]; then
			set_xclip
			sleep 0.2
			set_wlcopy
			sleep 0.3
			if ! wlcopy_has_target; then
				set_wlcopy
				sleep 0.3
			fi
			final_ok() { wlcopy_has_target; }
		else
			set_wlcopy
			sleep 0.2
			set_xclip
			sleep 0.3
			if ! xclip_has_target; then
				set_xclip
				sleep 0.3
			fi
			final_ok() { xclip_has_target; }
		fi

		echo "xclip: $(cat "$xclip_log")"
		echo "wl-copy: $(cat "$wlcopy_log")"
		final_ok
		""";

	public Task<(bool ok, string log)> SetClipboardImageAsync(
		string windowsPngPath, CancellationToken ct)
		=> SetClipboardContentAsync(windowsPngPath, "image/png", ct);

	// windowsFilePath is a file whose bytes ARE the clipboard content —
	// for a file *reference* (BuildFileUri), write the URI text to its
	// own temp file first and pass that instead.
	public async Task<(bool ok, string log)> SetClipboardContentAsync(
		string windowsFilePath, string mimeType, CancellationToken ct)
	{
		string wslPath = ToWslPath(windowsFilePath);
		string modeArg = Mode == ClipboardMode.Wayland ? "wayland" : "x11";
		string script = ScriptTemplate
			.Replace("__MODE__", modeArg)
			.Replace("__MIME__", mimeType);

		var psi = new ProcessStartInfo
		{
			FileName = "wsl.exe",
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};

		if (!string.IsNullOrEmpty(_distro))
		{
			psi.ArgumentList.Add("-d");
			psi.ArgumentList.Add(_distro);
		}

		psi.ArgumentList.Add("-e");
		psi.ArgumentList.Add("env");
		psi.ArgumentList.Add(DisplayEnv);
		psi.ArgumentList.Add(WaylandEnv);
		psi.ArgumentList.Add(RuntimeDirEnv);
		psi.ArgumentList.Add("bash");
		psi.ArgumentList.Add("-c");
		psi.ArgumentList.Add(script);
		psi.ArgumentList.Add(wslPath); // becomes $0 inside the script

		using var process = new Process { StartInfo = psi };
		process.Start();

		Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
		Task<string> stderrTask = process.StandardError.ReadToEndAsync(ct);

		using var timeoutCts = CancellationTokenSource
			.CreateLinkedTokenSource(ct);
		timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
		try
		{
			await process.WaitForExitAsync(timeoutCts.Token);
		}
		catch (OperationCanceledException)
		{
			TryKill(process);
			return (false, "wsl.exe timed out after 10s");
		}

		string stdout = await stdoutTask;
		string stderr = await stderrTask;
		string log = $"exit={process.ExitCode} stdout={stdout.Trim()} stderr={stderr.Trim()}";
		return (process.ExitCode == 0, log);
	}

	private static void TryKill(Process process)
	{
		try
		{
			process.Kill(entireProcessTree: true);
		}
		catch (InvalidOperationException)
		{
			// Already exited between the timeout firing and Kill().
		}
	}

	// WSL2's standard mount scheme: C:\Users\x\y -> /mnt/c/Users/x/y.
	// Every distro mounts fixed drives this way by default; a
	// custom /etc/wsl.conf [automount] root would break this, but so
	// would every other fixed-path assumption this tool already makes
	// (DISPLAY, WAYLAND_DISPLAY, XDG_RUNTIME_DIR).
	private static string ToWslPath(string windowsPath)
	{
		string full = Path.GetFullPath(windowsPath);
		char drive = char.ToLowerInvariant(full[0]);
		string rest = full[2..].Replace('\\', '/');
		return $"/mnt/{drive}{rest}";
	}

	// text/uri-list is the freedesktop-standard clipboard MIME type for
	// "here is a file", understood by GTK file managers and most
	// X11/Wayland apps that accept a pasted/dropped file. Whether Claude
	// Desktop's paste handler reacts to it (vs. only real OS drag-and-drop
	// events) is unverified — this makes the reference available on the
	// clipboard; it doesn't guarantee any given app acts on it.
	public string BuildFileUri(string windowsPath)
		=> new Uri("file://" + ToWslPath(windowsPath)).AbsoluteUri;
}
