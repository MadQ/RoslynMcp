namespace RoslynMcp.Cli;

class SetupCommand : CliCommand
{
    public static int Run()
    {
        var executablePath = ExecutablePath;

        if(executablePath is null)
        {
            Console.Error.WriteLine("error: could not determine the current executable path.");

            return 1;
        }

        Console.WriteLine();
        Console.WriteLine("RoslynMcp Setup");
        Console.WriteLine("  Configures RoslynMcp as an MCP server in your AI coding agents.");
        Console.WriteLine($"  Executable: {executablePath}");
        Console.WriteLine($"  Version:    v{CurrentVersion}");
        Console.WriteLine();

        var results = AgentDetector.ProbeAll();

        // Present numbered list of detected agents.
        Console.WriteLine("  Detected agents:")
;
        Console.WriteLine();

        var candidates = new List<(int Number, AgentProbeResult Result)>();

        for(var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            string note;

            if(!r.ConfigExists)
                note = "(config not found — will be created)";
            else if(r.Entry is not null)
                note = "(already configured — will update)";
            else
                note = "(config found — will add entry)";

            candidates.Add((i + 1, r));
            Console.WriteLine($"    {i + 1}. {r.Client.Name,-22}  {note}");
        }

        Console.WriteLine();
        Console.Write("  Which agents to configure? (e.g. 1,3 or 'all' or 'none'): ");
        var input = Console.ReadLine()?.Trim() ?? "";

        var selected = ParseSelection(input, candidates);

        if(selected is null)
        {
            Console.WriteLine("  Cancelled.");

            return 0;
        }

        if(selected.Count == 0)
        {
            Console.WriteLine("  Nothing selected.");

            return 0;
        }

        Console.WriteLine();

        // Warn about malformed configs before touching anything.
        if(!PreflightCheck(selected))

            return 1;

        WarnIfProcessesRunning();

        // Opt-in: interactive elicitation on ambiguous symbol matches (writes --elicit into args).
        // Default is No — the agent-first structured candidate list is the intended default.
        Console.WriteLine("  On an ambiguous symbol match, roslyn_preview_rename / roslyn_change_signature");
        Console.WriteLine("  return a structured candidate list the agent resolves on its own (default).");
        Console.WriteLine("  With --elicit they instead ask you to pick — but only in MCP clients that");
        Console.WriteLine("  support elicitation (Claude Code/Desktop do; many others don't and silently");
        Console.WriteLine("  fall back to the candidate list).");
        Console.Write("  Enable interactive elicitation (--elicit)? [y/N]: ");

        var elicitAnswer = Console.ReadLine()?.Trim() ?? "";
        var elicitMode   = elicitAnswer.Equals("y",   StringComparison.OrdinalIgnoreCase) ||
                           elicitAnswer.Equals("yes", StringComparison.OrdinalIgnoreCase)
            ? ElicitMode.Enable
            : ElicitMode.Disable;

        Console.WriteLine();

        Console.WriteLine($"  Configuring {selected.Count} agent(s)...");
        Console.WriteLine();

        var addedCount = 0;
        var updatedCount = 0;
        var failedCount = 0;
        var skippedCount = 0;

        foreach(var r in selected)
        {
            if(!ConfirmOverwriteForeignEntry(r))
            {
                Console.WriteLine($"  ↷ {r.Client.Name}  — skipped (left existing entry untouched)");
                Console.WriteLine();
                skippedCount++;
                continue;
            }

            var outcome = AgentConfigPatcher.Patch(r.ConfigPath, r.Client, executablePath, elicitMode);

            switch(outcome.Result)
            {
                case PatchResult.Added:
                    Console.WriteLine($"  ✓ {r.Client.Name}  — {(outcome.IsNewFile ? "created" : "added")}");
                    Console.WriteLine($"      {r.ConfigPath}");

                    if(outcome.BackupPath is not null)
                        Console.WriteLine($"      ⚠ Comments stripped, JSON reformatted. Backup: {outcome.BackupPath}");

                    addedCount++;
                    break;

                case PatchResult.Updated:
                    Console.WriteLine($"  ✓ {r.Client.Name}  — updated");
                    Console.WriteLine($"      {r.ConfigPath}");

                    if(outcome.BackupPath is not null)
                        Console.WriteLine($"      ⚠ Comments stripped, JSON reformatted. Backup: {outcome.BackupPath}");

                    updatedCount++;
                    break;

                case PatchResult.Failed:
                    Console.WriteLine($"  ✗ {r.Client.Name}  — {outcome.Error}");
                    Console.WriteLine($"      {r.ConfigPath}");

                    if(outcome.BackupPath is not null)
                        Console.WriteLine($"      Backup preserved at: {outcome.BackupPath}");

                    failedCount++;
                    break;
            }

            Console.WriteLine();
        }

        Console.WriteLine($"  Done: {addedCount} added, {updatedCount} updated, {failedCount} failed{(skippedCount > 0 ? $", {skippedCount} skipped" : "")}.");

        if(failedCount == 0)
        {
            Console.WriteLine();
            Console.WriteLine("  Restart your agents (or reload the MCP config) to pick up the changes.");
            Console.WriteLine();
            Console.WriteLine($"  RoslynMcp v{CurrentVersion} — feedback & issues: https://github.com/MadQ/RoslynMcp");
        }

        // Offer to set up the Claude Code global pre-tool-use advisor hook if Claude Code was
        // selected and successfully configured.
        var claudeResult = selected.FirstOrDefault(r => r.Client is ClaudeCodeClient);

        if(claudeResult is not null)
        {
            Console.WriteLine();
            Console.WriteLine("  Claude Code supports a user-level advisor hook that guides it to prefer");
            Console.WriteLine("  roslyn_* tools for .cs files — advisory only, nothing is blocked.");
            Console.Write("  Set up Claude Code global pre-tool-use advisor hook? [y/N]: ");

            var hookAnswer = Console.ReadLine()?.Trim() ?? "";

            if(hookAnswer.Equals("y", StringComparison.OrdinalIgnoreCase) ||
               hookAnswer.Equals("yes", StringComparison.OrdinalIgnoreCase))
            {
                const string hookCommand = ToolCommand.HookCommand;
                var ok = ((ClaudeCodeClient) claudeResult.Client).UpsertHook(hookCommand);

                Console.WriteLine();

                if(ok)
                    Console.WriteLine("  ✓ Claude Code hook added (applies to all future sessions).");
                else
                    Console.WriteLine("  ✗ Failed to update Claude Code hook — check ~/.claude.json permissions.");
            }
        }

        Console.WriteLine();
        Console.WriteLine("  Tip: run '" + ToolCommand.Name + " setup-project' in each project/repo directory to");
        Console.WriteLine("  enable per-project guidance for VS Code Copilot and Copilot CLI.");
        Console.WriteLine();

        return failedCount > 0 ? 1 : 0;
    }

    // Returns null on cancel, empty list on "none", populated list on valid selection.
    static List<AgentProbeResult>? ParseSelection(
        string input,
        List<(int Number, AgentProbeResult Result)> candidates)
    {
        if(input.Equals("none", StringComparison.OrdinalIgnoreCase))

            return [];

        if(input.Equals("all", StringComparison.OrdinalIgnoreCase))

            return candidates.Select(c => c.Result).ToList();

        if(string.IsNullOrWhiteSpace(input))

            return null;

        var selected = new List<AgentProbeResult>();

        foreach(var part in input.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if(!int.TryParse(part, out var num) || num < 1 || num > candidates.Count)
            {
                Console.WriteLine($"  Invalid selection: '{part}'");

                return null;
            }

            selected.Add(candidates.First(c => c.Number == num).Result);
        }

        return selected;
    }

    // Re-probe selected agents for malformed JSON before patching any of them.
    // Returns false if the user cancels.
    static bool PreflightCheck(List<AgentProbeResult> selected)
    {
        foreach(var r in selected)
        {
            if(!r.ConfigExists)
                continue;

            // Try parsing to catch malformed JSON before we back up and modify anything.
            try
            {
                var json = File.ReadAllText(r.ConfigPath);
                System.Text.Json.JsonDocument.Parse(json,
                    new System.Text.Json.JsonDocumentOptions {

                        AllowTrailingCommas = true,
                        CommentHandling = System.Text.Json.JsonCommentHandling.Skip
                    });
            }
            catch(System.Text.Json.JsonException ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  ⚠ {r.Client.Name}: malformed JSON in {r.ConfigPath}");
                Console.WriteLine($"      {ex.Message}");
                Console.ResetColor();
                Console.WriteLine();
                Console.Write("  [S]kip this agent / [V]iew file excerpt / [C]ancel  > ");

                var choice = Console.ReadLine()?.Trim().ToUpperInvariant() ?? "";
                Console.WriteLine();

                switch(choice)
                {
                    case "S":
                        selected.Remove(r);
                        break;

                    case "V":
                        PrintExcerpt(r.ConfigPath, ex.LineNumber);
                        Console.Write("  [S]kip this agent / [C]ancel  > ");
                        var choice2 = Console.ReadLine()?.Trim().ToUpperInvariant() ?? "";
                        Console.WriteLine();

                        if(choice2 != "S")

                            return false;

                        selected.Remove(r);
                        break;

                    default:

                        return false;
                }
            }
        }

        return true;
    }

    static void PrintExcerpt(string path, long? errorLine)
    {
        try
        {
            var lines = File.ReadAllLines(path);
            var start = (int) Math.Max(0, (errorLine ?? 1) - 3);
            var end = Math.Min(lines.Length, start + 6);

            Console.WriteLine();

            for(var i = start; i < end; i++)
                Console.WriteLine($"    {i + 1,4}: {lines[i]}");

            Console.WriteLine();
        }
        catch { }
    }


}
