using System.Text.Json;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Net;
using System.Net.Sockets;

namespace LethalSeedSimulator.Gui;

public partial class Form1 : Form
{
    private sealed record SetupStatus(string Message, double Percent);

    private sealed record MoonChoice(string Id, string Name)
    {
        public override string ToString() => $"{Id}: {Name}";
    }

    private readonly GuiServices services;
    private CancellationTokenSource? bulkCts;
    private readonly string appDataRoot;
    private int bulkRangeStart;
    private int bulkRangeEnd;
    private int viewerPage = 1;
    private string viewerSortColumn = "seed";
    private bool viewerSortDescending;
    private CancellationTokenSource? setupCts;
    private static readonly HttpClient http = new();

    public Form1()
    {
        InitializeComponent();
        appDataRoot = ResolveAppDataRoot();
        var rulesRoot = ResolveRulesRoot();
        services = new GuiServices(rulesRoot);
        txtGlobalInfo.Text = $"Data: {appDataRoot} | Rules: {rulesRoot}";
        txtSetupAssetRipper.Text = ResolveDefaultAssetRipperPath();
        txtSetupGameRoot.Text = ResolveDefaultGameRootPath();
        UpdateSetupStatus("Idle", 0);
        bulkRangeStart = 0;
        bulkRangeEnd = 99_999_999;
        txtBulkRange.Text = $"{bulkRangeStart:N0} - {bulkRangeEnd:N0}";
        SetBulkButtonsIdle(true);
        numBulkReportInterval.Value = 100000;
        numViewPageSize.Value = 200;
        numInspectWeatherSeed.Value = Math.Max(numInspectSeed.Value - 1, 0);
        TryShowMoonHints();
    }

    private async void btnInspect_Click(object sender, EventArgs e)
    {
        try
        {
            var moon = GetSelectedMoon(cmbInspectMoon);
            var runSeed = (int)numInspectSeed.Value;
            var weatherSeed = (int)numInspectWeatherSeed.Value;
            var report = await Task.Run(() => services.Inspect(txtInspectVersion.Text.Trim(), moon, runSeed, weatherSeed, 0, 0));
            txtInspectOutput.Text = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Inspect failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async void btnBulkRefresh_Click(object sender, EventArgs e)
    {
        try
        {
            await RefreshMoonProgressAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Refresh failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async void btnBulkSimulateSelected_Click(object sender, EventArgs e)
    {
        if (bulkCts is not null)
        {
            return;
        }

        var moon = GetSelectedBulkMoon();
        if (moon is null)
        {
            MessageBox.Show(this, "Select one moon from the list.", "Moon required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        bulkCts = new CancellationTokenSource();
        SetBulkButtonsIdle(false);
        txtBulkLog.AppendText($"Starting simulation for {moon.Name} at {DateTime.Now}\r\n");
        var progress = new Progress<string>(line => txtBulkLog.AppendText(line + "\r\n"));
        try
        {
            await Task.Run(() => services.SimulateRangeToMoonDb(
                appDataRoot,
                txtBulkVersion.Text.Trim(),
                moon.Id,
                bulkRangeStart,
                bulkRangeEnd,
                chkBulkForceResimulate.Checked,
                (int)numBulkReportInterval.Value,
                progress,
                bulkCts.Token));
            await RefreshMoonProgressAsync();
        }
        catch (OperationCanceledException)
        {
            txtBulkLog.AppendText("Simulation canceled by user.\r\n");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Simulation failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            bulkCts?.Dispose();
            bulkCts = null;
            SetBulkButtonsIdle(true);
        }
    }

    private async void btnBulkSimulateAll_Click(object sender, EventArgs e)
    {
        if (bulkCts is not null)
        {
            return;
        }

        var moons = lvBulkMoons.Items.Cast<ListViewItem>()
            .Select(x => x.Tag as MoonChoice)
            .Where(x => x is not null)
            .Cast<MoonChoice>()
            .ToList();
        if (moons.Count == 0)
        {
            MessageBox.Show(this, "No moons available to simulate.", "No moons", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        bulkCts = new CancellationTokenSource();
        SetBulkButtonsIdle(false);
        txtBulkLog.AppendText($"Starting full moon pass at {DateTime.Now}\r\n");
        var progress = new Progress<string>(line => txtBulkLog.AppendText(line + "\r\n"));
        try
        {
            foreach (var moon in moons)
            {
                if (bulkCts.IsCancellationRequested)
                {
                    break;
                }

                txtBulkLog.AppendText($"Moon {moon.Id}:{moon.Name}\r\n");
                await Task.Run(() => services.SimulateRangeToMoonDb(
                    appDataRoot,
                    txtBulkVersion.Text.Trim(),
                    moon.Id,
                    bulkRangeStart,
                    bulkRangeEnd,
                    chkBulkForceResimulate.Checked,
                    (int)numBulkReportInterval.Value,
                    progress,
                    bulkCts.Token));
            }

            await RefreshMoonProgressAsync();
        }
        catch (OperationCanceledException)
        {
            txtBulkLog.AppendText("Bulk run canceled by user.\r\n");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Bulk run failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            bulkCts?.Dispose();
            bulkCts = null;
            SetBulkButtonsIdle(true);
        }
    }

    private void btnBulkCancel_Click(object sender, EventArgs e)
    {
        bulkCts?.Cancel();
    }

    private void btnBulkOptions_Click(object sender, EventArgs e)
    {
        using var options = new BulkRangeOptionsForm(bulkRangeStart, bulkRangeEnd);
        if (options.ShowDialog(this) == DialogResult.OK)
        {
            bulkRangeStart = options.StartSeed;
            bulkRangeEnd = options.EndSeed;
            txtBulkRange.Text = $"{bulkRangeStart:N0} - {bulkRangeEnd:N0}";
        }
    }

    private async void btnViewLoad_Click(object sender, EventArgs e)
    {
        viewerPage = 1;
        await LoadViewerPageAsync();
    }

    private async void btnViewPrev_Click(object sender, EventArgs e)
    {
        if (viewerPage <= 1)
        {
            return;
        }

        viewerPage--;
        await LoadViewerPageAsync();
    }

    private async void btnViewNext_Click(object sender, EventArgs e)
    {
        viewerPage++;
        await LoadViewerPageAsync();
    }

    private async void dgvViewer_ColumnHeaderMouseClick(object sender, DataGridViewCellMouseEventArgs e)
    {
        var column = dgvViewer.Columns[e.ColumnIndex].Name;
        if (string.Equals(column, viewerSortColumn, StringComparison.OrdinalIgnoreCase))
        {
            viewerSortDescending = !viewerSortDescending;
        }
        else
        {
            viewerSortColumn = column;
            viewerSortDescending = false;
        }

        viewerPage = 1;
        await LoadViewerPageAsync();
    }

    private async void btnExtractRules_Click(object sender, EventArgs e)
    {
        try
        {
            btnExtractRules.Enabled = false;
            var repoRoot = ResolveSourceRootWithPrompt();
            await Task.Run(() => services.ExtractRulePack("decompiled-current", repoRoot));
            txtGlobalInfo.Text = $"Rulepack refreshed at {DateTime.Now:t} | Rules: {services.RulesRoot}";
            TryShowMoonHints();
            MessageBox.Show(this, "Rulepack extracted/refreshed successfully.", "Done", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Rule extraction failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            btnExtractRules.Enabled = true;
        }
    }

    private void btnSetupBrowseGameRoot_Click(object sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select folder containing Lethal Company.exe",
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            txtSetupGameRoot.Text = dialog.SelectedPath;
        }
    }

    private void btnSetupBrowseAssetRipper_Click(object sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Executable (*.exe)|*.exe|All Files (*.*)|*.*",
            Title = "Select AssetRipper CLI executable"
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            txtSetupAssetRipper.Text = dialog.FileName;
        }
    }

    private async void btnSetupExtract_Click(object sender, EventArgs e)
    {
        if (setupCts is not null)
        {
            return;
        }

        var gameRoot = txtSetupGameRoot.Text.Trim();
        var assetRipperExe = txtSetupAssetRipper.Text.Trim();
        setupCts = new CancellationTokenSource();
        btnSetupExtract.Enabled = false;
        txtSetupLog.Clear();
        UpdateSetupStatus("Starting extraction", 0);
        var progress = new Progress<string>(line => txtSetupLog.AppendText(line + "\r\n"));
        var status = new Progress<SetupStatus>(s => UpdateSetupStatus(s.Message, s.Percent));
        try
        {
            await Task.Run(() => SetupAndExtractRulepack(gameRoot, assetRipperExe, progress, status, setupCts.Token));
            txtGlobalInfo.Text = $"Rulepack refreshed at {DateTime.Now:t} | Rules: {services.RulesRoot}";
            TryShowMoonHints();
            UpdateSetupStatus("Done", 100);
            MessageBox.Show(this, "Setup complete. Rulepack refreshed.", "Done", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException)
        {
            txtSetupLog.AppendText("Setup canceled.\r\n");
            UpdateSetupStatus("Canceled", 0);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Setup failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            setupCts?.Dispose();
            setupCts = null;
            btnSetupExtract.Enabled = true;
        }
    }

    private async void btnSetupDownloadAssetRipper_Click(object sender, EventArgs e)
    {
        if (setupCts is not null)
        {
            return;
        }

        setupCts = new CancellationTokenSource();
        btnSetupDownloadAssetRipper.Enabled = false;
        txtSetupLog.Clear();
        UpdateSetupStatus("Preparing download", 0);
        var progress = new Progress<string>(line => txtSetupLog.AppendText(line + "\r\n"));
        var status = new Progress<SetupStatus>(s => UpdateSetupStatus(s.Message, s.Percent));
        try
        {
            var exe = await DownloadAssetRipperAsync(progress, status, setupCts.Token);
            txtSetupAssetRipper.Text = exe;
            UpdateSetupStatus("Download complete", 100);
            txtSetupLog.AppendText($"Installed: {exe}\r\n");
        }
        catch (OperationCanceledException)
        {
            txtSetupLog.AppendText("Download canceled.\r\n");
            UpdateSetupStatus("Canceled", 0);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Download failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            setupCts?.Dispose();
            setupCts = null;
            btnSetupDownloadAssetRipper.Enabled = true;
        }
    }

    private void SetBulkButtonsIdle(bool idle)
    {
        btnBulkSimulateSelected.Enabled = idle;
        btnBulkSimulateAll.Enabled = idle;
        btnBulkCancel.Enabled = !idle;
    }

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "Assembly-CSharp")) &&
                Directory.Exists(Path.Combine(current.FullName, "Assets", "MonoBehaviour")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not resolve repository root.");
    }

    private static string ResolveRulesRoot()
    {
        var fromEnv = Environment.GetEnvironmentVariable("LETHAL_SIM_RULES_ROOT");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv;
        }

        var appRules = Path.Combine(ResolveAppDataRoot(), "rules");
        Directory.CreateDirectory(appRules);
        return appRules;
    }

    private string ResolveSourceRootWithPrompt()
    {
        var fromEnv = Environment.GetEnvironmentVariable("LETHAL_SIM_SOURCE_ROOT");
        if (!string.IsNullOrWhiteSpace(fromEnv) &&
            Directory.Exists(Path.Combine(fromEnv, "Assembly-CSharp")) &&
            Directory.Exists(Path.Combine(fromEnv, "Assets", "MonoBehaviour")))
        {
            return fromEnv;
        }

        try
        {
            return ResolveRepoRoot();
        }
        catch
        {
        }

        using var dialog = new FolderBrowserDialog
        {
            Description = "Select folder containing Assembly-CSharp and Assets/MonoBehaviour",
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog(this) == DialogResult.OK &&
            Directory.Exists(Path.Combine(dialog.SelectedPath, "Assembly-CSharp")) &&
            Directory.Exists(Path.Combine(dialog.SelectedPath, "Assets", "MonoBehaviour")))
        {
            return dialog.SelectedPath;
        }

        throw new InvalidOperationException(
            "Source root not selected. Set LETHAL_SIM_SOURCE_ROOT or choose a valid folder.");
    }

    private void TryShowMoonHints()
    {
        try
        {
            var levels = services.GetLevels("decompiled-current");
            if (levels.Count == 0)
            {
                return;
            }

            var hints = string.Join(", ", levels.Take(8).Select(x => $"{x.Id}:{x.Name}"));
            txtGlobalInfo.Text = $"Data: {appDataRoot} | Rules: {services.RulesRoot} | Moons loaded: {levels.Count} | {hints}";
            PopulateMoonCombos(levels);
            _ = RefreshMoonProgressAsync();
        }
        catch
        {
        }
    }

    private void PopulateMoonCombos(IReadOnlyList<LethalSeedSimulator.Rules.LevelRule> levels)
    {
        var choices = levels.Select(x => new MoonChoice(x.Id, x.Name)).ToList();
        BindCombo(cmbInspectMoon, choices);
        BindCombo(cmbViewMoon, choices);
    }

    private static void BindCombo(ComboBox combo, IReadOnlyList<MoonChoice> choices)
    {
        var previous = combo.SelectedItem as MoonChoice;
        combo.BeginUpdate();
        combo.Items.Clear();
        combo.Items.AddRange(choices.Cast<object>().ToArray());
        combo.EndUpdate();

        if (choices.Count == 0)
        {
            return;
        }

        if (previous is not null)
        {
            var match = choices.FirstOrDefault(x => x.Id == previous.Id);
            if (match is not null)
            {
                combo.SelectedItem = match;
                return;
            }
        }

        combo.SelectedIndex = 0;
    }

    private static string GetSelectedMoon(ComboBox combo)
    {
        return combo.SelectedItem is MoonChoice moon ? moon.Id : combo.Text.Trim();
    }

    private MoonChoice? GetSelectedBulkMoon()
    {
        if (lvBulkMoons.SelectedItems.Count == 0)
        {
            return null;
        }

        return lvBulkMoons.SelectedItems[0].Tag as MoonChoice;
    }

    private async Task RefreshMoonProgressAsync()
    {
        btnBulkRefresh.Enabled = false;
        try
        {
            var version = txtBulkVersion.Text.Trim();
            var progressRows = await Task.Run(() => services.GetMoonProgress(appDataRoot, version));
            lvBulkMoons.BeginUpdate();
            lvBulkMoons.Items.Clear();
            foreach (var row in progressRows)
            {
                var item = new ListViewItem($"{row.MoonId}: {row.MoonName}")
                {
                    Tag = new MoonChoice(row.MoonId, row.MoonName)
                };
                item.SubItems.Add(row.SimulatedCount.ToString("N0"));
                item.SubItems.Add($"{row.Percent:F4}%");
                lvBulkMoons.Items.Add(item);
            }

            lvBulkMoons.EndUpdate();
        }
        finally
        {
            btnBulkRefresh.Enabled = true;
        }
    }

    private async Task LoadViewerPageAsync()
    {
        var moon = GetSelectedMoon(cmbViewMoon);
        if (string.IsNullOrWhiteSpace(moon))
        {
            return;
        }

        btnViewLoad.Enabled = false;
        try
        {
            int? min = int.TryParse(txtViewMinTotal.Text.Trim(), out var minParsed) ? minParsed : null;
            int? max = int.TryParse(txtViewMaxTotal.Text.Trim(), out var maxParsed) ? maxParsed : null;
            var page = await Task.Run(() => services.QueryMoonRows(
                appDataRoot,
                txtViewVersion.Text.Trim(),
                moon,
                viewerSortColumn,
                viewerSortDescending,
                min,
                max,
                viewerPage,
                (int)numViewPageSize.Value));
            if (viewerPage > 1 && page.Rows.Count == 0)
            {
                viewerPage--;
                return;
            }

            dgvViewer.Rows.Clear();
            foreach (var row in page.Rows)
            {
                dgvViewer.Rows.Add(
                    row.Seed,
                    row.TotalScrapValue,
                    row.ScrapCount,
                    row.Weather,
                    row.KeyCount,
                    row.DungeonFlowTheme,
                    row.ApparatusValue);
            }

            var totalPages = page.TotalCount == 0 ? 1 : (int)Math.Ceiling(page.TotalCount / (double)(int)numViewPageSize.Value);
            lblViewPage.Text = $"Page {viewerPage} / {totalPages} ({page.TotalCount:N0} rows)";
            btnViewPrev.Enabled = viewerPage > 1;
            btnViewNext.Enabled = viewerPage < totalPages;
        }
        finally
        {
            btnViewLoad.Enabled = true;
        }
    }

    private static string ResolveAppDataRoot()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LethalSeedSimulator");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string ResolveDefaultAssetRipperPath()
    {
        var fromEnv = Environment.GetEnvironmentVariable("LETHAL_SIM_ASSETRIPPER_CLI");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv;
        }

        return string.Empty;
    }

    private async Task<string> DownloadAssetRipperAsync(IProgress<string> progress, IProgress<SetupStatus> status, CancellationToken cancellationToken)
    {
        status.Report(new SetupStatus("Resolving release", 5));
        progress.Report("Resolving latest AssetRipper release from GitHub...");
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/AssetRipper/AssetRipper/releases/latest");
        req.Headers.UserAgent.ParseAdd("LethalSeedSimulator");
        using var response = await http.SendAsync(req, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "latest";
        var archName = RuntimeInformation.OSArchitecture == Architecture.Arm64
            ? "AssetRipper_win_arm64.zip"
            : "AssetRipper_win_x64.zip";
        var assets = root.GetProperty("assets");
        string? downloadUrl = null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? string.Empty;
            if (string.Equals(name, archName, StringComparison.OrdinalIgnoreCase))
            {
                downloadUrl = asset.GetProperty("browser_download_url").GetString();
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            throw new InvalidOperationException($"Could not find release asset '{archName}'.");
        }

        var toolsRoot = Path.Combine(appDataRoot, "tools", "assetripper", tag);
        Directory.CreateDirectory(toolsRoot);
        var zipPath = Path.Combine(toolsRoot, archName);
        var extractRoot = Path.Combine(toolsRoot, "current");

        status.Report(new SetupStatus($"Downloading {archName}", 10));
        progress.Report($"Downloading {archName}...");
        using (var downloadResponse = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            downloadResponse.EnsureSuccessStatusCode();
            var contentLength = downloadResponse.Content.Headers.ContentLength;
            await using var downloadStream = await downloadResponse.Content.ReadAsStreamAsync(cancellationToken);
            await using var fs = File.Create(zipPath);
            var buffer = new byte[1024 * 128];
            long totalRead = 0;
            while (true)
            {
                var read = await downloadStream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                await fs.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                totalRead += read;
                if (contentLength is > 0)
                {
                    var pct = 10.0 + (totalRead * 70.0 / contentLength.Value);
                    status.Report(new SetupStatus($"Downloading {archName}", Math.Min(80.0, pct)));
                }
            }
        }

        if (Directory.Exists(extractRoot))
        {
            Directory.Delete(extractRoot, true);
        }

        Directory.CreateDirectory(extractRoot);
        status.Report(new SetupStatus("Extracting zip", 85));
        progress.Report("Extracting AssetRipper...");
        ZipFile.ExtractToDirectory(zipPath, extractRoot, true);

        var exeCandidates = new[]
        {
            Path.Combine(extractRoot, "AssetRipper.CLI.exe"),
            Path.Combine(extractRoot, "AssetRipper.GUI.Free.exe")
        };
        var exe = exeCandidates.FirstOrDefault(File.Exists);
        if (exe is null)
        {
            throw new InvalidOperationException("No AssetRipper executable found after extraction.");
        }

        status.Report(new SetupStatus("Ready", 100));
        progress.Report($"AssetRipper ready: {exe}");
        return exe;
    }

    private static string ResolveDefaultGameRootPath()
    {
        var fromEnv = Environment.GetEnvironmentVariable("LETHAL_SIM_GAME_ROOT");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv;
        }

        return string.Empty;
    }

    private void SetupAndExtractRulepack(string gameRoot, string assetRipperExe, IProgress<string> progress, IProgress<SetupStatus> status, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot))
        {
            throw new InvalidOperationException("Game root folder is missing.");
        }

        var exePath = Path.Combine(gameRoot, "Lethal Company.exe");
        if (!File.Exists(exePath))
        {
            throw new InvalidOperationException("Could not find Lethal Company.exe in the selected game root.");
        }

        if (string.IsNullOrWhiteSpace(assetRipperExe) || !File.Exists(assetRipperExe))
        {
            throw new InvalidOperationException("AssetRipper CLI executable path is missing or invalid.");
        }

        var setupRoot = Path.Combine(appDataRoot, "setup");
        var ripperOut = Path.Combine(setupRoot, "ripper-output");
        var sourceRoot = Path.Combine(setupRoot, "rulepack-source");
        Directory.CreateDirectory(setupRoot);
        if (Directory.Exists(ripperOut))
        {
            Directory.Delete(ripperOut, true);
        }

        if (Directory.Exists(sourceRoot))
        {
            Directory.Delete(sourceRoot, true);
        }

        Directory.CreateDirectory(ripperOut);
        Directory.CreateDirectory(sourceRoot);
        status.Report(new SetupStatus("Validating inputs", 5));
        progress.Report($"Game root: {gameRoot}");
        progress.Report($"Running AssetRipper: {assetRipperExe}");
        status.Report(new SetupStatus("Extracting with AssetRipper", 20));
        RunAssetRipperWithFallbackArgs(assetRipperExe, gameRoot, ripperOut, progress, cancellationToken);

        var candidate = FindExtractedRoot(ripperOut);
        if (candidate is null)
        {
            throw new InvalidOperationException("AssetRipper output did not contain Assets/MonoBehaviour.");
        }

        status.Report(new SetupStatus("Filtering required data", 70));
        progress.Report($"Using extracted root: {candidate}");
        var assetsSource = Path.Combine(candidate, "Assets");
        var assetsTarget = Path.Combine(sourceRoot, "Assets");
        Directory.CreateDirectory(assetsTarget);
        CopyDirectory(Path.Combine(assetsSource, "MonoBehaviour"), Path.Combine(assetsTarget, "MonoBehaviour"));
        CopyDirectory(Path.Combine(assetsSource, "GameObject"), Path.Combine(assetsTarget, "GameObject"));
        CopyDirectory(Path.Combine(assetsSource, "Scenes"), Path.Combine(assetsTarget, "Scenes"));

        var assemblyCsharpSource = Path.Combine(candidate, "Assembly-CSharp");
        if (!Directory.Exists(assemblyCsharpSource))
        {
            var scriptsFallback = Path.Combine(candidate, "Assets", "Scripts", "Assembly-CSharp");
            if (Directory.Exists(scriptsFallback))
            {
                assemblyCsharpSource = scriptsFallback;
            }
            else
            {
                throw new InvalidOperationException("Extracted output is missing Assembly-CSharp source files.");
            }
        }

        CopyDirectory(assemblyCsharpSource, Path.Combine(sourceRoot, "Assembly-CSharp"));
        status.Report(new SetupStatus("Building rulepack", 90));
        progress.Report("Building rulepack from extracted minimal data...");
        services.ExtractRulePack("decompiled-current", sourceRoot);
        status.Report(new SetupStatus("Completed", 100));
        progress.Report("Rulepack extraction completed.");
    }

    private static void RunAssetRipperWithFallbackArgs(string assetRipperExe, string gameRoot, string outputRoot, IProgress<string> progress, CancellationToken cancellationToken)
    {
        var probeArgs = "\"C:\\__lc_probe_in__\" \"C:\\__lc_probe_out__\"";
        var probe = RunProcessCapture(assetRipperExe, probeArgs);
        if (probe.ExitCode != 0 && probe.Output.Contains("Too many arguments were supplied.", StringComparison.OrdinalIgnoreCase))
        {
            progress.Report("AssetRipper argument mode unavailable. Switching to headless web API mode.");
            RunAssetRipperViaWebApi(assetRipperExe, gameRoot, outputRoot, progress, cancellationToken);
            return;
        }

        var attempts = new[]
        {
            $"\"{gameRoot}\" \"{outputRoot}\"",
            $"\"{gameRoot}\" -o \"{outputRoot}\"",
            $"-i \"{gameRoot}\" -o \"{outputRoot}\""
        };

        var failures = new List<string>();
        foreach (var args in attempts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report($"AssetRipper args: {args}");
            var exit = RunProcess(assetRipperExe, args, progress, cancellationToken);
            if (exit == 0)
            {
                return;
            }

            failures.Add($"{args} -> exit {exit}");
        }

        throw new InvalidOperationException("AssetRipper failed for all argument formats: " + string.Join(" | ", failures));
    }

    private static void RunAssetRipperViaWebApi(string assetRipperExe, string gameRoot, string outputRoot, IProgress<string> progress, CancellationToken cancellationToken)
    {
        var port = GetFreeTcpPort();
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = assetRipperExe,
            Arguments = $"--headless --port {port}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                progress.Report(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                progress.Report(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var baseUrl = $"http://127.0.0.1:{port}";
        var start = DateTime.UtcNow;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                throw new InvalidOperationException($"AssetRipper exited early with code {process.ExitCode}.");
            }

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/openapi.json");
                using var resp = http.Send(req, cancellationToken);
                if (resp.IsSuccessStatusCode)
                {
                    break;
                }
            }
            catch
            {
            }

            if (DateTime.UtcNow - start > TimeSpan.FromSeconds(60))
            {
                throw new InvalidOperationException("Timed out waiting for AssetRipper headless server to start.");
            }

            Thread.Sleep(500);
        }

        progress.Report("AssetRipper headless API is ready.");
        PostForm($"{baseUrl}/Reset", new Dictionary<string, string>(), cancellationToken);
        PostForm($"{baseUrl}/LoadFolder", new Dictionary<string, string> { ["path"] = gameRoot }, cancellationToken);
        PostForm($"{baseUrl}/Export/UnityProject", new Dictionary<string, string> { ["path"] = outputRoot }, cancellationToken);

        if (!process.HasExited)
        {
            process.Kill(true);
            process.WaitForExit();
        }
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static void PostForm(string url, IReadOnlyDictionary<string, string> data, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(data);
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        using var resp = http.Send(req, cancellationToken);
        if ((int)resp.StatusCode >= 400)
        {
            throw new InvalidOperationException($"AssetRipper API call failed: {url} => {(int)resp.StatusCode} {resp.ReasonPhrase}");
        }
    }

    private static int RunProcess(string fileName, string arguments, IProgress<string> progress, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                progress.Report(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                progress.Report(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        while (!process.WaitForExit(250))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        return process.ExitCode;
    }

    private static (int ExitCode, string Output) RunProcessCapture(string fileName, string arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout + Environment.NewLine + stderr);
    }

    private static string? FindExtractedRoot(string root)
    {
        if (Directory.Exists(Path.Combine(root, "Assets", "MonoBehaviour")))
        {
            return root;
        }

        foreach (var dir in Directory.GetDirectories(root, "*", SearchOption.AllDirectories))
        {
            if (Directory.Exists(Path.Combine(dir, "Assets", "MonoBehaviour")))
            {
                return dir;
            }
        }

        return null;
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        if (!Directory.Exists(sourceDir))
        {
            throw new InvalidOperationException($"Required directory not found: {sourceDir}");
        }

        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var fileName = Path.GetFileName(file);
            File.Copy(file, Path.Combine(targetDir, fileName), true);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var name = Path.GetFileName(dir);
            CopyDirectory(dir, Path.Combine(targetDir, name));
        }
    }

    private sealed class BulkRangeOptionsForm : Form
    {
        private readonly NumericUpDown numStart;
        private readonly NumericUpDown numEnd;

        public BulkRangeOptionsForm(int startSeed, int endSeed)
        {
            Text = "Bulk range options";
            Width = 360;
            Height = 170;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            var labelStart = new Label { Left = 18, Top = 20, Width = 80, Text = "Start seed" };
            var labelEnd = new Label { Left = 18, Top = 55, Width = 80, Text = "End seed" };
            numStart = new NumericUpDown { Left = 110, Top = 18, Width = 220, Maximum = 99_999_999, Value = Math.Clamp(startSeed, 0, 99_999_999) };
            numEnd = new NumericUpDown { Left = 110, Top = 53, Width = 220, Maximum = 99_999_999, Value = Math.Clamp(endSeed, 0, 99_999_999) };
            var ok = new Button { Left = 174, Top = 90, Width = 75, Text = "OK", DialogResult = DialogResult.OK };
            var cancel = new Button { Left = 255, Top = 90, Width = 75, Text = "Cancel", DialogResult = DialogResult.Cancel };
            Controls.Add(labelStart);
            Controls.Add(labelEnd);
            Controls.Add(numStart);
            Controls.Add(numEnd);
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        public int StartSeed => (int)numStart.Value;

        public int EndSeed => (int)numEnd.Value;
    }

    private void UpdateSetupStatus(string message, double percent)
    {
        var clamped = Math.Max(0, Math.Min(100, percent));
        progressSetup.Value = (int)Math.Round(clamped * 10.0);
        lblSetupStatus.Text = $"{message} ({clamped:F1}%)";
    }
}
