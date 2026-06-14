using System.IO;

namespace StayVibin.Services;

/// <summary>
/// Writes small Python helper modules onto the agent-server's import path:
///  - sv_tool_aliases.py registers tool-name ALIASES (e.g. "search" -> grep,
///    "execute_powershell" -> terminal) so a local model that calls a tool by a
///    different-but-understood name is routed instead of failing "tool not found".
///    It also monkeypatches a real bug in the upstream grep tool's include filter
///    (see below) so content searches with a path-style include like "**/*.cpp"
///    actually return results instead of always reporting "No files found".
///  - sv_probe.py reports which tools the engine actually has registered, so we
///    only ever put confirmed tools in the agent spec (an unregistered tool in the
///    spec makes conversation creation fail).
///
/// We never modify the engine itself: these files live in our own app-data folder
/// and that folder is added to PYTHONPATH only when StayVibin launches the server.
/// </summary>
public static class EngineExtensions
{
    /// <summary>Importable module name, referenced from tool_module_qualnames.</summary>
    public const string ModuleName = "sv_tool_aliases";

    /// <summary>Probe script file name (run once with the engine's Python).</summary>
    public const string ProbeScript = "sv_probe.py";

    /// <summary>
    /// Synonym tool names the alias module registers. Each maps to a real tool:
    /// the shell synonyms -> terminal, "search" -> grep, "find" -> glob. Keep this
    /// list in sync with the class names in <see cref="AliasModuleSource"/> (the
    /// engine derives the tool name from the class name: snake_case minus "_tool").
    /// </summary>
    public static readonly string[] AliasTools =
    {
        "bash", "shell", "cmd", "powershell",
        "execute_bash", "execute_powershell", "run_command",  // -> terminal
        "search",  // -> grep
        "find",    // -> glob
    };

    // The alias module. Defensive by design: any failure is swallowed so it can
    // never break agent-server startup or conversation creation. REGISTERED lists
    // the aliases that registered OK (read back by the probe).
    private const string AliasModuleSource =
        "# StayVibin engine extension: tool-name aliases.\n" +
        "# Registers common synonym tool names as thin aliases of the real engine\n" +
        "# tools so a local model that calls a tool by a different-but-understood\n" +
        "# name (e.g. 'search' for grep, 'execute_powershell' for the terminal) is\n" +
        "# routed to the correct tool instead of failing with 'tool not found'.\n" +
        "# Each alias subclasses a real tool, inheriting the same action schema and\n" +
        "# executor; only the tool NAME differs (derived from the class name).\n" +
        "REGISTERED = []\n" +
        "try:\n" +
        "    from openhands.sdk.tool import register_tool\n" +
        "    from openhands.tools.terminal.definition import TerminalTool\n" +
        "    from openhands.tools.grep.definition import GrepTool\n" +
        "    from openhands.tools.glob.definition import GlobTool\n" +
        "\n" +
        "    # Shell synonyms -> terminal (identical 'command' action schema).\n" +
        "    class BashTool(TerminalTool): pass\n" +
        "    class ShellTool(TerminalTool): pass\n" +
        "    class CmdTool(TerminalTool): pass\n" +
        "    class PowershellTool(TerminalTool): pass\n" +
        "    class ExecuteBashTool(TerminalTool): pass\n" +
        "    class ExecutePowershellTool(TerminalTool): pass\n" +
        "    class RunCommandTool(TerminalTool): pass\n" +
        "\n" +
        "    # Search synonyms.\n" +
        "    class SearchTool(GrepTool): pass    # content search -> grep\n" +
        "    class FindTool(GlobTool): pass      # file-name search -> glob\n" +
        "\n" +
        "    for _alias in (\n" +
        "        BashTool, ShellTool, CmdTool, PowershellTool,\n" +
        "        ExecuteBashTool, ExecutePowershellTool, RunCommandTool,\n" +
        "        SearchTool, FindTool,\n" +
        "    ):\n" +
        "        try:\n" +
        "            register_tool(_alias.name, _alias)\n" +
        "            REGISTERED.append(_alias.name)\n" +
        "        except Exception:\n" +
        "            pass\n" +
        "except Exception:\n" +
        "    pass\n" +
        "\n" +
        "# Fix for the upstream grep include-filter bug. The grep tool matches the\n" +
        "# include glob against the BARE FILENAME, so any path-style include that\n" +
        "# contains '/' (e.g. '**/*.cpp') never matches and EVERY result is discarded -\n" +
        "# the search reports 'No files found' even though ripgrep located matches. That\n" +
        "# makes the agent (any model) flail, re-search, and give up. We replace the\n" +
        "# filter so basename globs ('*.cpp'), path globs ('**/*.cpp', 'src/**/*.cpp'),\n" +
        "# and simple {a,b} brace sets ('*.{ts,tsx}') all match correctly.\n" +
        "try:\n" +
        "    import fnmatch as _sv_fnmatch\n" +
        "    import re as _sv_re\n" +
        "    from openhands.tools.grep.impl import GrepExecutor as _SvGrepExecutor\n" +
        "\n" +
        "    def _sv_include_matches(filename, rel_posix, include_pattern):\n" +
        "        patterns = [include_pattern]\n" +
        "        _m = _sv_re.search(r'\\{([^{}]*)\\}', include_pattern)\n" +
        "        if _m:\n" +
        "            _pre = include_pattern[:_m.start()]\n" +
        "            _post = include_pattern[_m.end():]\n" +
        "            patterns = [_pre + _opt + _post for _opt in _m.group(1).split(',')]\n" +
        "        for _pat in patterns:\n" +
        "            _last = _pat.rsplit('/', 1)[-1]\n" +
        "            if (_sv_fnmatch.fnmatch(filename, _pat)\n" +
        "                    or _sv_fnmatch.fnmatch(rel_posix, _pat)\n" +
        "                    or _sv_fnmatch.fnmatch(filename, _last)):\n" +
        "                return True\n" +
        "        return False\n" +
        "\n" +
        "    def _sv_path_matches_filters(self, path, search_path, include_pattern):\n" +
        "        try:\n" +
        "            relative_parts = path.resolve().relative_to(\n" +
        "                search_path.resolve()).parts\n" +
        "        except Exception:\n" +
        "            relative_parts = (path.name,)\n" +
        "        if any(_p.startswith('.') for _p in relative_parts[:-1]):\n" +
        "            return False\n" +
        "        filename = relative_parts[-1] if relative_parts else path.name\n" +
        "        if not include_pattern:\n" +
        "            return not filename.startswith('.')\n" +
        "        rel_posix = '/'.join(relative_parts)\n" +
        "        return _sv_include_matches(filename, rel_posix, include_pattern)\n" +
        "\n" +
        "    _SvGrepExecutor._path_matches_filters = _sv_path_matches_filters\n" +
        "except Exception:\n" +
        "    pass\n";

    // One-shot probe: import the optional tool modules + the alias module, then
    // print the names the engine reports as USABLE. StayVibin reads this to decide
    // which optional/alias tools are safe to put in the agent spec. We use usable
    // (not just registered) so a tool with a usability check - e.g. the browser
    // toolset, which needs a Chromium binary - is only enabled when it will work.
    private const string ProbeSource =
        "import json, sys\n" +
        "for _m in (\n" +
        "    'openhands.tools.grep.definition',\n" +
        "    'openhands.tools.glob.definition',\n" +
        "    'openhands.tools.terminal.definition',\n" +
        "    'openhands.tools.browser_use.definition',\n" +
        "    'sv_tool_aliases',\n" +
        "):\n" +
        "    try:\n" +
        "        __import__(_m)\n" +
        "    except Exception:\n" +
        "        pass\n" +
        "try:\n" +
        "    from openhands.sdk.tool.registry import list_usable_tools\n" +
        "    _names = sorted(set(list_usable_tools()))\n" +
        "except Exception:\n" +
        "    try:\n" +
        "        from openhands.sdk.tool.registry import list_registered_tools\n" +
        "        _names = sorted(set(list_registered_tools()))\n" +
        "    except Exception:\n" +
        "        _names = []\n" +
        "sys.stdout.write('SVT:' + json.dumps(_names))\n";

    /// <summary>
    /// Ensure both helper modules exist on disk and return the directory to add to
    /// PYTHONPATH. Best-effort: returns null if the files could not be written (the
    /// server still runs, just without aliases). Rewritten every call so updates to
    /// the alias set always take effect.
    /// </summary>
    public static string? EnsureModuleDir()
    {
        try
        {
            var dir = Path.Combine(AppPaths.Root, "engine_ext");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, ModuleName + ".py"), AliasModuleSource);
            File.WriteAllText(Path.Combine(dir, ProbeScript), ProbeSource);
            return dir;
        }
        catch
        {
            return null;
        }
    }
}
