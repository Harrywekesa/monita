using System.Globalization;

namespace TrafficMonitor;

internal sealed class MainForm : Form
{
    private readonly TrafficCollector _collector = new();
    private readonly DataGridView _grid = new();
    private readonly ProcessController _processController = new();
    private readonly FirewallBlockManager _firewallManager;
    private readonly ComboBox _dateSelector = new();
    private readonly Label _status = new();
    private readonly Label _bundleUsage = new();
    private readonly Label _targetStatus = new();
    private readonly Label _networkStatus = new();
    private readonly Label _appLimitUsage = new();
    private readonly NumericUpDown _limitInput = new();
    private readonly NumericUpDown _resetDayInput = new();
    private readonly NumericUpDown _appLimitInput = new();
    private readonly Button _pauseButton = new();
    private readonly Button _endButton = new();
    private readonly Button _networkPauseButton = new();
    private readonly Button _connectionsButton = new();
    private readonly CheckBox _startWithWindows = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new();
    private readonly NotifyIcon _trayIcon;
    private readonly HashSet<string> _alertedThresholds = new(StringComparer.OrdinalIgnoreCase);
    private bool _loadingDate;
    private bool _allowExit;
    private DateOnly _dateListDay;
    private DateTime _lastAlertCheck = DateTime.MinValue;
    private DateTime _lastUsageDisplayRefresh = DateTime.MinValue;
    private DateTime _lastFlush = DateTime.MinValue;
    private string _budgetEditorImagePath = "";
    private readonly bool _startHidden;
    private IReadOnlyList<FirewallBlock> _activeBlocks = Array.Empty<FirewallBlock>();
    private string _firewallError = "";

    public MainForm(bool startHidden = false)
    {
        _startHidden = startHidden;
        Text = "Monita - App Traffic";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        _firewallManager = new FirewallBlockManager(_collector.DataDirectory, _processController);
        Width = 1050;
        Height = 620;
        MinimumSize = new Size(800, 420);
        StartPosition = FormStartPosition.CenterScreen;

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 78,
            Padding = new Padding(8, 8, 8, 4),
            WrapContents = true
        };
        toolbar.Controls.Add(new Label
        {
            Text = "Daily totals:",
            AutoSize = true,
            Margin = new Padding(0, 5, 6, 0)
        });
        _dateSelector.DropDownStyle = ComboBoxStyle.DropDownList;
        _dateSelector.FormattingEnabled = true;
        _dateSelector.Width = 150;
        _dateSelector.SelectedIndexChanged += (_, _) => RefreshRows();
        _dateSelector.Format += FormatDate;
        toolbar.Controls.Add(_dateSelector);
        toolbar.Controls.Add(new Label
        {
            Text = "Bundle limit (GB):",
            AutoSize = true,
            Margin = new Padding(12, 5, 4, 0)
        });
        _limitInput.DecimalPlaces = 1;
        _limitInput.Increment = 1;
        _limitInput.Maximum = 100000;
        _limitInput.Width = 80;
        toolbar.Controls.Add(_limitInput);
        toolbar.Controls.Add(new Label
        {
            Text = "Reset day:",
            AutoSize = true,
            Margin = new Padding(8, 5, 4, 0)
        });
        _resetDayInput.Minimum = 1;
        _resetDayInput.Maximum = 28;
        _resetDayInput.Width = 55;
        toolbar.Controls.Add(_resetDayInput);
        var saveSettings = new Button
        {
            Text = "Save",
            AutoSize = true,
            Margin = new Padding(8, 1, 0, 0)
        };
        saveSettings.Click += SaveBundleSettings;
        toolbar.Controls.Add(saveSettings);
        _bundleUsage.AutoSize = true;
        _bundleUsage.Margin = new Padding(12, 5, 0, 0);
        toolbar.Controls.Add(_bundleUsage);
        _startWithWindows.AutoSize = true;
        _startWithWindows.Text = "Start with Windows";
        _startWithWindows.Margin = new Padding(12, 4, 0, 0);
        _startWithWindows.Click += ToggleStartup;
        toolbar.Controls.Add(_startWithWindows);

        var processActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 38,
            Padding = new Padding(8, 3, 8, 3),
            WrapContents = false
        };
        _targetStatus.AutoSize = true;
        _targetStatus.Text = "Select a current-day app to manage it.";
        _targetStatus.Margin = new Padding(0, 5, 10, 0);
        processActions.Controls.Add(_targetStatus);
        _pauseButton.Text = "Pause";
        _pauseButton.AutoSize = true;
        _pauseButton.Enabled = false;
        _pauseButton.Click += PauseOrResumeSelected;
        processActions.Controls.Add(_pauseButton);
        _endButton.Text = "End app";
        _endButton.AutoSize = true;
        _endButton.Enabled = false;
        _endButton.Click += EndSelected;
        processActions.Controls.Add(_endButton);
        _networkPauseButton.Text = "Block network (30 min)";
        _networkPauseButton.AutoSize = true;
        _networkPauseButton.Enabled = false;
        _networkPauseButton.Click += BlockOrUnblockNetwork;
        processActions.Controls.Add(_networkPauseButton);
        _networkStatus.AutoSize = true;
        _networkStatus.Margin = new Padding(10, 5, 0, 0);
        processActions.Controls.Add(_networkStatus);

        var featureActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 38,
            Padding = new Padding(8, 3, 8, 3),
            WrapContents = false
        };
        featureActions.Controls.Add(new Label
        {
            Text = "App limit (GB):",
            AutoSize = true,
            Margin = new Padding(0, 5, 5, 0)
        });
        _appLimitInput.DecimalPlaces = 1;
        _appLimitInput.Increment = 1;
        _appLimitInput.Maximum = 100000;
        _appLimitInput.Width = 80;
        featureActions.Controls.Add(_appLimitInput);
        var saveAppLimit = new Button { Text = "Set app cap", AutoSize = true };
        saveAppLimit.Click += SaveAppLimit;
        featureActions.Controls.Add(saveAppLimit);
        _appLimitUsage.AutoSize = true;
        _appLimitUsage.Margin = new Padding(6, 5, 0, 0);
        featureActions.Controls.Add(_appLimitUsage);
        _connectionsButton.Text = "Connections";
        _connectionsButton.AutoSize = true;
        _connectionsButton.Enabled = false;
        _connectionsButton.Click += ShowConnections;
        featureActions.Controls.Add(_connectionsButton);
        var reports = new Button { Text = "Reports / CSV", AutoSize = true };
        reports.Click += (_, _) => new ReportsForm(_collector).ShowDialog(this);
        featureActions.Controls.Add(reports);
        var diagnostics = new Button { Text = "System diagnostics", AutoSize = true };
        diagnostics.Click += (_, _) => ShowDiagnostics();
        featureActions.Controls.Add(diagnostics);

        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AutoGenerateColumns = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.RowHeadersVisible = false;
        _grid.SelectionChanged += (_, _) => UpdateProcessActions();
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(UsageRecord.Application),
            HeaderText = "App",
            FillWeight = 85
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(UsageRecord.Services),
            HeaderText = "Services in process",
            FillWeight = 125
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(UsageRecord.Adapter),
            HeaderText = "Adapter",
            FillWeight = 90
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(UsageRecord.SentBytes),
            HeaderText = "Sent",
            FillWeight = 75
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(UsageRecord.ReceivedBytes),
            HeaderText = "Received",
            FillWeight = 75
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(UsageRecord.TotalBytes),
            HeaderText = "Total",
            FillWeight = 75
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(UsageRecord.SentBytesPerSecond),
            HeaderText = "Upload/s",
            FillWeight = 65
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(UsageRecord.ReceivedBytesPerSecond),
            HeaderText = "Download/s",
            FillWeight = 70
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(UsageRecord.ProcessId),
            HeaderText = "PID",
            FillWeight = 40
        });
        _grid.CellFormatting += FormatByteColumns;

        _status.Dock = DockStyle.Bottom;
        _status.Height = 30;
        _status.Padding = new Padding(8, 6, 8, 0);
        _status.Text = "Starting...";

        Controls.Add(_grid);
        Controls.Add(_status);
        Controls.Add(processActions);
        Controls.Add(featureActions);
        Controls.Add(toolbar);

        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Open Monita", null, (_, _) => RestoreWindow());
        trayMenu.Items.Add("Exit", null, (_, _) =>
        {
            _allowExit = true;
            Close();
        });
        _trayIcon = new NotifyIcon
        {
            Icon = Icon,
            Text = "Monita traffic monitor",
            ContextMenuStrip = trayMenu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => RestoreWindow();

        _refreshTimer.Interval = 1000;
        _refreshTimer.Tick += (_, _) =>
        {
            try
            {
                var today = DateOnly.FromDateTime(DateTime.Now);
                if (today != _dateListDay)
                {
                    RefreshDateList();
                }

                var now = DateTime.Now;
                _status.Text = $"{_collector.Status}  |  Data: {_collector.DataDirectory}";
                if (now - _lastUsageDisplayRefresh >= TimeSpan.FromSeconds(5))
                {
                    _lastUsageDisplayRefresh = now;
                    RefreshBundleUsage();
                }

                RefreshRows();
                if (now - _lastFlush >= TimeSpan.FromSeconds(10))
                {
                    _lastFlush = now;
                    _collector.Flush();
                }

                RefreshNetworkBlocks();
                UpdateProcessActions();
                if (DateTime.Now - _lastAlertCheck >= TimeSpan.FromSeconds(15))
                {
                    _lastAlertCheck = DateTime.Now;
                    CheckUsageNotifications();
                }
            }
            catch (Exception exception)
            {
                _status.Text = $"Status update error: {exception.Message}";
            }
        };
        _refreshTimer.Start();

        Shown += (_, _) =>
        {
            var settings = _collector.Settings;
            _limitInput.Value = (decimal)settings.MonthlyLimitGB;
            _resetDayInput.Value = settings.ResetDay;
            _startWithWindows.Checked = settings.StartWithWindows;
            RefreshDateList();
            _collector.Start();
            RefreshNetworkBlocks();
            UpdateProcessActions();
            CheckUsageNotifications();
            if (_startHidden)
            {
                Hide();
            }
        };

        FormClosing += (_, e) =>
        {
            if (!_allowExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                _trayIcon.ShowBalloonTip(2500, "Monita is still monitoring",
                    "Open Monita from the system tray to view usage.", ToolTipIcon.Info);
            }
        };

        FormClosed += (_, _) =>
        {
            _refreshTimer.Stop();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            try
            {
                _processController.Dispose();
            }
            catch (AggregateException exception)
            {
                ShowProcessActionError(exception);
            }
            finally
            {
                _collector.Dispose();
            }
        };
    }

    private void RefreshDateList()
    {
        var current = DateOnly.FromDateTime(DateTime.Now);
        _dateListDay = current;
        var dates = _collector.GetAvailableDates().ToHashSet();
        dates.Add(current);

        _loadingDate = true;
        _dateSelector.Items.Clear();
        foreach (var date in dates.OrderByDescending(date => date))
        {
            _dateSelector.Items.Add(date);
        }
        _dateSelector.SelectedItem = current;
        _loadingDate = false;
    }

    private void FormatDate(object? sender, ListControlConvertEventArgs e)
    {
        if (e.ListItem is DateOnly date)
        {
            e.Value = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
    }

    private void RefreshRows()
    {
        if (_loadingDate || _dateSelector.SelectedItem is not DateOnly date)
        {
            return;
        }

        var selectedRecord = GetSelectedRecord();
        _grid.DataSource = _collector.GetRecords(date);
        if (selectedRecord is not null)
        {
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.DataBoundItem is UsageRecord record
                    && record.ProcessId == selectedRecord.ProcessId
                    && string.Equals(record.ImagePath, selectedRecord.ImagePath, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(record.Adapter, selectedRecord.Adapter, StringComparison.OrdinalIgnoreCase))
                {
                    row.Selected = true;
                    break;
                }
            }
        }
    }

    private UsageRecord? GetSelectedRecord()
    {
        return _grid.SelectedRows.Count > 0
            ? _grid.SelectedRows[0].DataBoundItem as UsageRecord
            : null;
    }

    private void UpdateProcessActions()
    {
        var selected = GetSelectedRecord();
        var selectedDate = _dateSelector.SelectedItem is DateOnly date ? date : default;
        var canControl = _processController.CanControl(selected, selectedDate);
        var isPaused = selected is not null && _processController.IsSuspended(selected.ProcessId);
        var activeBlock = selected is null
            ? null
            : _activeBlocks.FirstOrDefault(block =>
                string.Equals(block.ImagePath, selected.ImagePath, StringComparison.OrdinalIgnoreCase));
        _pauseButton.Text = isPaused ? "Resume" : "Pause";
        _pauseButton.Enabled = canControl && (isPaused || selected is not null);
        _endButton.Enabled = canControl && !isPaused;
        _networkPauseButton.Text = activeBlock is null
            ? "Block network (30 min)"
            : "Resume network";
        _networkPauseButton.Enabled = activeBlock is not null || canControl;
        _connectionsButton.Enabled = selected is not null && selected.Connections.Count > 0;
        _networkStatus.Text = activeBlock is null
            ? ""
            : $"Blocked until {activeBlock.ExpiresAt:t} ({Math.Max(0, (int)activeBlock.TimeRemaining.TotalMinutes)} min left)";
        _targetStatus.Text = selected is null
            ? "Select a current-day app to manage it."
            : canControl
                ? $"{selected.Application} (PID {selected.ProcessId})"
                : "Only current-day, non-system apps without hosted services can be managed.";
        if (!string.IsNullOrWhiteSpace(_firewallError))
        {
            _networkPauseButton.Enabled = false;
            _networkStatus.Text = _firewallError;
        }

        if (!_appLimitInput.Focused && !string.Equals(_budgetEditorImagePath, selected?.ImagePath, StringComparison.OrdinalIgnoreCase))
        {
            _budgetEditorImagePath = selected?.ImagePath ?? "";
            var limits = _collector.GetAppLimits();
            _appLimitInput.Value = selected is not null
                && limits.TryGetValue(selected.ImagePath, out var limit)
                ? Math.Clamp((decimal)limit, _appLimitInput.Minimum, _appLimitInput.Maximum)
                : 0;
        }
    }

    private void PauseOrResumeSelected(object? sender, EventArgs e)
    {
        var selected = GetSelectedRecord();
        if (selected is null)
        {
            return;
        }

        try
        {
            if (_processController.IsSuspended(selected.ProcessId))
            {
                _processController.Resume(selected);
            }
            else if (MessageBox.Show(this,
                $"Pause {selected.Application} (PID {selected.ProcessId})? It will stop responding until resumed.",
                "Pause app", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            {
                _processController.Pause(selected);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or ArgumentException
            or System.ComponentModel.Win32Exception
            or System.Management.ManagementException)
        {
            ShowProcessActionError(exception);
        }

        UpdateProcessActions();
    }

    private void EndSelected(object? sender, EventArgs e)
    {
        var selected = GetSelectedRecord();
        if (selected is null
            || MessageBox.Show(this,
                $"End {selected.Application} (PID {selected.ProcessId})? Unsaved work may be lost.",
                "End app", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        try
        {
            _processController.End(selected);
            RefreshRows();
            _grid.ClearSelection();
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or ArgumentException
            or System.ComponentModel.Win32Exception
            or System.Management.ManagementException)
        {
            ShowProcessActionError(exception);
        }

        UpdateProcessActions();
    }

    private void ShowProcessActionError(Exception exception)
    {
        MessageBox.Show(this, exception.Message, "Could not manage app",
            MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void SaveAppLimit(object? sender, EventArgs e)
    {
        var selected = GetSelectedRecord();
        if (selected is null || !Path.IsPathFullyQualified(selected.ImagePath))
        {
            MessageBox.Show(this, "Select an app with a known executable path first.",
                "App limit", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            _collector.SetAppLimit(selected.ImagePath, (double)_appLimitInput.Value);
            RefreshBundleUsage();
            CheckUsageNotifications();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(this, exception.Message, "Could not save app limit",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ShowConnections(object? sender, EventArgs e)
    {
        var selected = GetSelectedRecord();
        if (selected is not null)
        {
            new ConnectionsForm(selected).ShowDialog(this);
        }
    }

    private void ShowDiagnostics()
    {
        var date = _dateSelector.SelectedItem is DateOnly selectedDate
            ? selectedDate
            : DateOnly.FromDateTime(DateTime.Today);
        new DiagnosticsForm(_collector, date).ShowDialog(this);
    }

    private void BlockOrUnblockNetwork(object? sender, EventArgs e)
    {
        var selected = GetSelectedRecord();
        if (selected is null)
        {
            return;
        }

        var activeBlock = _activeBlocks.FirstOrDefault(block =>
            string.Equals(block.ImagePath, selected.ImagePath, StringComparison.OrdinalIgnoreCase));
        try
        {
            if (activeBlock is not null)
            {
                if (MessageBox.Show(this, $"Restore network access for {selected.Application}?",
                    "Resume network", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    _firewallManager.Unblock(activeBlock.Id);
                }
            }
            else if (MessageBox.Show(this,
                $"Block inbound and outbound access for all instances of {selected.Application} for 30 minutes? This adds temporary Windows Firewall rules.",
                "Block network access", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            {
                _firewallManager.Block(selected, TimeSpan.FromMinutes(30));
                _trayIcon.ShowBalloonTip(3000, "Network access paused",
                    $"{selected.Application} is blocked for up to 30 minutes.", ToolTipIcon.Info);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or System.Runtime.InteropServices.COMException
            or System.Management.ManagementException)
        {
            MessageBox.Show(this, exception.Message, "Could not change network access",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        RefreshNetworkBlocks();
        UpdateProcessActions();
    }

    private void RefreshNetworkBlocks()
    {
        try
        {
            _activeBlocks = _firewallManager.GetActiveBlocks();
            _firewallError = "";
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or System.Runtime.InteropServices.COMException)
        {
            _firewallError = $"Firewall cleanup failed: {exception.Message}";
        }
    }

    private void ToggleStartup(object? sender, EventArgs e)
    {
        var enabled = _startWithWindows.Checked;
        if (enabled && MessageBox.Show(this,
            "Monita will run hidden at sign-in with administrator privileges. Keep the executable in this location after enabling startup.",
            "Enable Monita startup", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            _startWithWindows.Checked = false;
            return;
        }

        var previous = !enabled;
        try
        {
            StartupTaskManager.SetEnabled(enabled);
            try
            {
                _collector.SetStartWithWindows(enabled);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or System.Text.Json.JsonException)
            {
                StartupTaskManager.SetEnabled(previous);
                throw;
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _startWithWindows.Checked = previous;
            MessageBox.Show(this, exception.Message, "Could not update startup task",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void CheckUsageNotifications()
    {
        var usage = _collector.GetBundleUsage();
        NotifyThreshold($"bundle:{usage.PeriodStart:yyyy-MM-dd}", "Total bundle",
            usage.Bytes / (1024d * 1024 * 1024), usage.MonthlyLimitGB);

        var totals = _collector.GetTotals(usage.PeriodStart, usage.PeriodEnd);
        var selected = GetSelectedRecord();
        if (selected is not null
            && totals.AppTotals.TryGetValue(selected.ImagePath, out var selectedBytes)
            && _collector.GetAppLimits().TryGetValue(selected.ImagePath, out var selectedLimit))
        {
            _appLimitUsage.Text =
                $"{Path.GetFileNameWithoutExtension(selected.ImagePath)}: {selectedBytes / (1024d * 1024 * 1024):0.##}/{selectedLimit:0.##} GB this cycle";
        }
        else
        {
            _appLimitUsage.Text = "";
        }

        foreach (var pair in _collector.GetAppLimits())
        {
            var usedGB = totals.AppBytes(pair.Key) / (1024d * 1024 * 1024);
            NotifyThreshold($"app:{usage.PeriodStart:yyyy-MM-dd}:{pair.Key}",
                Path.GetFileNameWithoutExtension(pair.Key), usedGB, pair.Value);
        }
    }

    private void NotifyThreshold(string key, string name, double usedGB, double limitGB)
    {
        if (limitGB <= 0)
        {
            return;
        }

        var percentage = usedGB / limitGB;
        var threshold = percentage >= 1 ? 100 : percentage >= 0.8 ? 80 : 0;
        if (threshold == 0 || !_alertedThresholds.Add($"{key}:{threshold}"))
        {
            return;
        }

        var message = threshold == 100
            ? $"{name} has reached its {limitGB:0.##} GB limit."
            : $"{name} has used {percentage:P0} of its {limitGB:0.##} GB limit.";
        _trayIcon.ShowBalloonTip(5000, "Monita data warning", message, ToolTipIcon.Warning);
    }

    private void RestoreWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        ShowInTaskbar = true;
        Activate();
    }

    private void SaveBundleSettings(object? sender, EventArgs e)
    {
        try
        {
            _collector.SetBundleSettings((double)_limitInput.Value, (int)_resetDayInput.Value);
            RefreshBundleUsage();
        }
        catch (IOException exception)
        {
            MessageBox.Show(this, exception.Message, "Could not save bundle settings",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (UnauthorizedAccessException exception)
        {
            MessageBox.Show(this, exception.Message, "Could not save bundle settings",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RefreshBundleUsage()
    {
        var usage = _collector.GetBundleUsage();
        var usedGB = usage.Bytes / (1024d * 1024 * 1024);
        if (usage.MonthlyLimitGB <= 0)
        {
            _bundleUsage.Text = $"Cycle {usage.PeriodStart:MMM d}–{usage.PeriodEnd.AddDays(-1):MMM d}: {usedGB:0.##} GB used (set a limit)";
            _bundleUsage.ForeColor = SystemColors.ControlText;
            return;
        }

        var percentage = usedGB / usage.MonthlyLimitGB;
        _bundleUsage.Text = $"Cycle {usage.PeriodStart:MMM d}–{usage.PeriodEnd.AddDays(-1):MMM d}: {usedGB:0.##} / {usage.MonthlyLimitGB:0.##} GB ({percentage:P0})";
        _bundleUsage.ForeColor = percentage >= 1
            ? Color.Firebrick
            : percentage >= 0.8
                ? Color.DarkOrange
                : SystemColors.ControlText;
    }

    private static void FormatByteColumns(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.Value is long bytes && e.ColumnIndex is >= 3 and <= 5)
        {
            e.Value = FormatBytes(bytes);
            e.FormattingApplied = true;
        }
        else if (e.Value is double bytesPerSecond && e.ColumnIndex is 6 or 7)
        {
            e.Value = $"{FormatBytes((long)bytesPerSecond)}/s";
            e.FormattingApplied = true;
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }
}
