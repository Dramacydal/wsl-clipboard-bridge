namespace WslClipboardBridge;

// The tray icon and the hidden watcher form share one ApplicationContext
// so closing from the tray menu shuts the whole message loop down
// cleanly instead of leaving the hidden form's handle running.
internal sealed class TrayContext : ApplicationContext
{
	private readonly ClipboardWatcherForm _watcherForm;
	private readonly NotifyIcon _trayIcon;
	private readonly ToolStripMenuItem _statusItem;

	public TrayContext(BridgeSettings settings)
	{
		_watcherForm = new ClipboardWatcherForm(settings);
		_watcherForm.StatusChanged += OnStatusChanged;
		_watcherForm.Mode = ModeStore.Load();
		// Access Handle to force native window creation now, while
		// staying invisible (SetVisibleCore override in the form).
		_ = _watcherForm.Handle;

		_statusItem = new ToolStripMenuItem("Watching clipboard…")
		{
			Enabled = false,
		};

		var targetItem = new ToolStripMenuItem(
			$"Distro: {settings.Distro ?? "(default)"}")
		{
			Enabled = false,
		};

		var enabledItem = new ToolStripMenuItem("Enabled")
		{
			CheckOnClick = true,
			Checked = true,
		};
		enabledItem.CheckedChanged += (_, _) =>
		{
			_watcherForm.WatchingEnabled = enabledItem.Checked;
			OnStatusChanged(enabledItem.Checked
				? "Watching clipboard…"
				: "Disabled");
		};

		var modeX11Item = new ToolStripMenuItem("X11");
		var modeWaylandItem = new ToolStripMenuItem("Wayland");

		void UpdateModeChecks(ClipboardMode mode)
		{
			modeX11Item.Checked = mode == ClipboardMode.X11;
			modeWaylandItem.Checked = mode == ClipboardMode.Wayland;
		}
		UpdateModeChecks(_watcherForm.Mode);

		void SelectMode(ClipboardMode mode)
		{
			_watcherForm.Mode = mode;
			UpdateModeChecks(mode);
			ModeStore.Save(mode);
		}
		modeX11Item.Click += (_, _) => SelectMode(ClipboardMode.X11);
		modeWaylandItem.Click += (_, _) => SelectMode(ClipboardMode.Wayland);

		var modeMenu = new ToolStripMenuItem("Mode");
		modeMenu.DropDownItems.Add(modeX11Item);
		modeMenu.DropDownItems.Add(modeWaylandItem);

		var exitItem = new ToolStripMenuItem("Exit");
		exitItem.Click += (_, _) => ExitThread();

		var menu = new ContextMenuStrip();
		menu.Items.Add(_statusItem);
		menu.Items.Add(targetItem);
		menu.Items.Add(new ToolStripSeparator());
		menu.Items.Add(enabledItem);
		menu.Items.Add(modeMenu);
		menu.Items.Add(new ToolStripSeparator());
		menu.Items.Add(exitItem);

		_trayIcon = new NotifyIcon
		{
			// Reads back the icon embedded into this exe by the
			// <ApplicationIcon>icon.ico</ApplicationIcon> csproj
			// setting, rather than loading icon.ico from disk again —
			// one source of truth, no runtime path to keep in sync.
			Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath)
				?? SystemIcons.Application,
			Text = "WSL Clipboard Bridge",
			Visible = true,
			ContextMenuStrip = menu,
		};
	}

	private void OnStatusChanged(string status)
	{
		// WndProc runs on the UI thread and so does this callback (no
		// cross-thread marshaling needed), but keep it defensive in case
		// that ever changes.
		if (_statusItem.GetCurrentParent()?.InvokeRequired == true)
		{
			_statusItem.GetCurrentParent()!.Invoke(
				() => _statusItem.Text = status);
			return;
		}

		_statusItem.Text = status;
	}

	protected override void ExitThreadCore()
	{
		_trayIcon.Visible = false;
		_trayIcon.Dispose();
		_watcherForm.Dispose();
		base.ExitThreadCore();
	}
}
