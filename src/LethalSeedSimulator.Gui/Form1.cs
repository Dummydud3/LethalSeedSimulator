using System.Text;
using System.Text.Json;

namespace LethalSeedSimulator.Gui;

public partial class Form1 : Form
{
    private sealed record MoonChoice(string Id, string Name)
    {
        public override string ToString() => $"{Id}: {Name}";
    }

    private readonly GuiServices services;
    private CancellationTokenSource? exportCts;

    public Form1()
    {
        InitializeComponent();
        var rulesRoot = ResolveRulesRoot();
        services = new GuiServices(rulesRoot);
        txtGlobalInfo.Text = $"Rules: {rulesRoot}";
        SetExportButtonsIdle(true);
        TryShowMoonHints();
    }

    private async void btnInspect_Click(object sender, EventArgs e)
    {
        try
        {
            var moon = GetSelectedMoon(cmbInspectMoon);
            var report = await Task.Run(() => services.Inspect(txtInspectVersion.Text.Trim(), moon, (int)numInspectSeed.Value));
            txtInspectOutput.Text = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Inspect failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async void btnSearch_Click(object sender, EventArgs e)
    {
        try
        {
            btnSearch.Enabled = false;
            txtSearchOutput.Text = "Searching...\r\n";
            var hits = await Task.Run(() => services.Search(
                txtSearchVersion.Text.Trim(),
                GetSelectedMoon(cmbSearchMoon),
                (int)numSearchStart.Value,
                (int)numSearchEnd.Value,
                txtSearchQuery.Text.Trim(),
                Environment.ProcessorCount));

            var sb = new StringBuilder();
            foreach (var hit in hits)
            {
                sb.AppendLine($"{hit.Seed}: weather={hit.Weather}, scrap={hit.ScrapCount}, total={hit.TotalScrapValue}");
            }
            sb.AppendLine($"Matches: {hits.Count}");
            txtSearchOutput.Text = sb.ToString();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Search failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            btnSearch.Enabled = true;
        }
    }

    private async void btnStartExport_Click(object sender, EventArgs e)
    {
        if (exportCts is not null)
        {
            return;
        }

        exportCts = new CancellationTokenSource();
        SetExportButtonsIdle(false);
        txtExportLog.AppendText($"Starting export at {DateTime.Now}\r\n");
        var progress = new Progress<string>(line => txtExportLog.AppendText(line + "\r\n"));

        try
        {
            await Task.Run(() => services.ExportCsv(
                txtExportVersion.Text.Trim(),
                GetSelectedMoon(cmbExportMoon),
                (int)numExportStart.Value,
                (int)numExportEnd.Value,
                txtExportOutputFile.Text.Trim(),
                (int)numExportReportInterval.Value,
                chkExportIncludeRollsJson.Checked,
                progress,
                exportCts.Token));
        }
        catch (OperationCanceledException)
        {
            txtExportLog.AppendText("Export canceled by user.\r\n");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            exportCts?.Dispose();
            exportCts = null;
            SetExportButtonsIdle(true);
        }
    }

    private void btnCancelExport_Click(object sender, EventArgs e)
    {
        exportCts?.Cancel();
    }

    private void btnBrowseExport_Click(object sender, EventArgs e)
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "CSV Files (*.csv)|*.csv|All Files (*.*)|*.*",
            FileName = Path.GetFileName(txtExportOutputFile.Text),
            InitialDirectory = Path.GetDirectoryName(txtExportOutputFile.Text)
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            txtExportOutputFile.Text = dialog.FileName;
        }
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

    private void SetExportButtonsIdle(bool idle)
    {
        btnStartExport.Enabled = idle;
        btnCancelExport.Enabled = !idle;
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

        var cwdRules = Path.Combine(Directory.GetCurrentDirectory(), "rules");
        if (Directory.Exists(cwdRules))
        {
            return cwdRules;
        }

        var nestedRules = Path.Combine(Directory.GetCurrentDirectory(), "LethalSeedSimulator", "rules");
        if (Directory.Exists(nestedRules))
        {
            return nestedRules;
        }

        return cwdRules;
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
            txtGlobalInfo.Text = $"Rules: {services.RulesRoot} | Moons loaded: {levels.Count} | {hints}";
            PopulateMoonCombos(levels);
        }
        catch
        {
        }
    }

    private void PopulateMoonCombos(IReadOnlyList<LethalSeedSimulator.Rules.LevelRule> levels)
    {
        var choices = levels.Select(x => new MoonChoice(x.Id, x.Name)).ToList();
        BindCombo(cmbInspectMoon, choices);
        BindCombo(cmbSearchMoon, choices);
        BindCombo(cmbExportMoon, choices);
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
}
