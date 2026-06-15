using System.IO;

namespace StayVibin.Services;

/// <summary>
/// Writes small Python helper modules onto the agent-server's import path:
///  - sv_tool_aliases.py is StayVibin's tool translator. It:
///     * registers tool-name ALIASES (e.g. "search" -> grep, "edit_file" ->
///       file_editor, "execute_powershell" -> terminal) so a local model that
///       calls a tool by a different-but-understood name is routed instead of
///       failing "tool not found";
///     * normalizes tool ARGUMENT keys to each tool's real schema fields (e.g.
///       "file"/"filename" -> path, "content"/"text" -> file_text, "query" ->
///       pattern) by patching ToolDefinition.action_from_arguments, so a model
///       that uses its own argument lingo still drives the tool instead of
///       failing the engine's strict (extra="forbid") schema validation;
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
        "terminal_command", "run_terminal_cmd", "run_shell",  // -> terminal
        "search", "grep_search", "ripgrep",                  // -> grep
        "find", "fd",                                         // -> glob
        "read_file", "open_file", "view_file", "cat_file",   // -> file_editor
        "edit_file", "write_file", "create_file",            // -> file_editor
        "modify_file", "replace_in_file", "apply_patch",     // -> file_editor
        "list_dir", "list_files", "glob_search", "file_search", // -> glob
        "codebase_search",                                   // -> grep
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
        "    from openhands.tools.file_editor.definition import FileEditorTool\n" +
        "\n" +
        "    # Shell synonyms -> terminal (identical 'command' action schema).\n" +
        "    class BashTool(TerminalTool): pass\n" +
        "    class ShellTool(TerminalTool): pass\n" +
        "    class CmdTool(TerminalTool): pass\n" +
        "    class PowershellTool(TerminalTool): pass\n" +
        "    class ExecuteBashTool(TerminalTool): pass\n" +
        "    class ExecutePowershellTool(TerminalTool): pass\n" +
        "    class RunCommandTool(TerminalTool): pass\n" +
        "    class TerminalCommandTool(TerminalTool): pass\n" +
        "    class RunTerminalCmdTool(TerminalTool): pass\n" +
        "    class RunShellTool(TerminalTool): pass\n" +
        "\n" +
        "    # Search synonyms.\n" +
        "    class SearchTool(GrepTool): pass    # content search -> grep\n" +
        "    class GrepSearchTool(GrepTool): pass\n" +
        "    class RipgrepTool(GrepTool): pass\n" +
        "    class CodebaseSearchTool(GrepTool): pass\n" +
        "    class FindTool(GlobTool): pass      # file-name search -> glob\n" +
        "    class FdTool(GlobTool): pass\n" +
        "    class GlobSearchTool(GlobTool): pass\n" +
        "    class FileSearchTool(GlobTool): pass\n" +
        "    class ListDirTool(GlobTool): pass\n" +
        "    class ListFilesTool(GlobTool): pass\n" +
        "\n" +
        "    # File-editing synonyms -> file_editor (same command/path/file_text schema).\n" +
        "    class ReadFileTool(FileEditorTool): pass\n" +
        "    class OpenFileTool(FileEditorTool): pass\n" +
        "    class ViewFileTool(FileEditorTool): pass\n" +
        "    class CatFileTool(FileEditorTool): pass\n" +
        "    class EditFileTool(FileEditorTool): pass\n" +
        "    class WriteFileTool(FileEditorTool): pass\n" +
        "    class CreateFileTool(FileEditorTool): pass\n" +
        "    class ModifyFileTool(FileEditorTool): pass\n" +
        "    class ReplaceInFileTool(FileEditorTool): pass\n" +
        "    class ApplyPatchTool(FileEditorTool): pass\n" +
        "\n" +
        "    for _alias in (\n" +
        "        BashTool, ShellTool, CmdTool, PowershellTool,\n" +
        "        ExecuteBashTool, ExecutePowershellTool, RunCommandTool,\n" +
        "        TerminalCommandTool, RunTerminalCmdTool, RunShellTool,\n" +
        "        SearchTool, GrepSearchTool, RipgrepTool, CodebaseSearchTool,\n" +
        "        FindTool, FdTool, GlobSearchTool, FileSearchTool,\n" +
        "        ListDirTool, ListFilesTool,\n" +
        "        ReadFileTool, OpenFileTool, ViewFileTool, CatFileTool,\n" +
        "        EditFileTool, WriteFileTool, CreateFileTool,\n" +
        "        ModifyFileTool, ReplaceInFileTool, ApplyPatchTool,\n" +
        "    ):\n" +
        "        try:\n" +
        "            register_tool(_alias.name, _alias)\n" +
        "            REGISTERED.append(_alias.name)\n" +
        "        except Exception:\n" +
        "            pass\n" +
        "except Exception:\n" +
        "    pass\n" +
        "\n" +
        "# StayVibin tool-argument translator. Local models frequently call the right\n" +
        "# tool but name its arguments with their own lingo (e.g. 'file'/'filename' for\n" +
        "# 'path', 'content'/'text' for 'file_text', 'query' for 'pattern'). The engine's\n" +
        "# action schemas use extra='forbid', so a single wrong key fails the whole call\n" +
        "# and the model loops. We patch the base ToolDefinition.action_from_arguments to\n" +
        "# rename known synonym keys to the tool's REAL field names BEFORE validation.\n" +
        "# It is scoped per-tool (only fields the specific tool actually has are\n" +
        "# considered), so the same synonym maps correctly for different tools and there\n" +
        "# is never cross-tool ambiguity. Canonical keys are never overwritten, unknown\n" +
        "# keys are left alone (they fail exactly as before), and any error falls back to\n" +
        "# the original behavior - so this can only make tool calls MORE likely to work.\n" +
        "try:\n" +
        "    from openhands.sdk.tool.tool import ToolDefinition as _SvToolDef\n" +
        "\n" +
        "    # Map of REAL field name -> set of accepted synonyms (already normalized to\n" +
        "    # lowercase with underscores). Add new lingo here as models surface it.\n" +
        "    _SV_FIELD_SYNONYMS = {\n" +
        "        'path': (\n" +
        "            'file', 'filename', 'file_name', 'filepath', 'file_path', 'fname',\n" +
        "            'target', 'target_file', 'targetfile', 'dir', 'directory', 'folder',\n" +
        "            'pathname', 'location', 'filepath_or_dir',\n" +
        "        ),\n" +
        "        'file_text': (\n" +
        "            'content', 'contents', 'text', 'body', 'data', 'file_content',\n" +
        "            'file_contents', 'new_content', 'source', 'code', 'file_data',\n" +
        "            'full_text', 'full_content',\n" +
        "        ),\n" +
        "        'old_str': (\n" +
        "            'old_string', 'old', 'old_text', 'oldstr', 'search', 'find',\n" +
        "            'from', 'original', 'target_text', 'old_content',\n" +
        "        ),\n" +
        "        'new_str': (\n" +
        "            'new_string', 'new', 'new_text', 'newstr', 'replace', 'replacement',\n" +
        "            'to', 'replace_with', 'new_content',\n" +
        "        ),\n" +
        "        'pattern': (\n" +
        "            'query', 'q', 'search_term', 'searchterm', 'search', 'regex',\n" +
        "            'keyword', 'keywords', 'expr', 'expression', 'text', 'term',\n" +
        "            'search_query', 'search_pattern', 'glob', 'name',\n" +
        "        ),\n" +
        "        'include': (\n" +
        "            'file_pattern', 'filter', 'include_pattern', 'file_glob',\n" +
        "            'glob_pattern', 'file_type', 'file_filter', 'extension', 'ext',\n" +
        "        ),\n" +
        "        'command': (\n" +
        "            'cmd', 'action', 'subcommand', 'operation', 'op', 'instruction',\n" +
        "            'shell_command', 'shell_cmd', 'bash_command', 'commandline',\n" +
        "            'command_line', 'script',\n" +
        "        ),\n" +
        "        'view_range': (\n" +
        "            'range', 'lines', 'line_range', 'linerange', 'view_lines',\n" +
        "        ),\n" +
        "        'insert_line': (\n" +
        "            'line', 'line_number', 'lineno', 'at_line', 'line_num',\n" +
        "        ),\n" +
        "    }\n" +
        "\n" +
        "    def _sv_remap_keys(action_type, arguments):\n" +
        "        if not isinstance(arguments, dict):\n" +
        "            return arguments\n" +
        "        try:\n" +
        "            valid = set(action_type.model_fields.keys())\n" +
        "        except Exception:\n" +
        "            return arguments\n" +
        "        out = dict(arguments)\n" +
        "        for _key in list(out.keys()):\n" +
        "            if _key in valid:\n" +
        "                continue\n" +
        "            _norm = str(_key).strip().lower().replace('-', '_').replace(' ', '_')\n" +
        "            # Direct normalized hit (e.g. 'File_Path' -> 'file_path' -> field).\n" +
        "            if _norm in valid and _norm not in out:\n" +
        "                out[_norm] = out.pop(_key)\n" +
        "                continue\n" +
        "            # Synonym lookup, scoped to fields THIS tool actually has.\n" +
        "            _target = None\n" +
        "            for _field, _syns in _SV_FIELD_SYNONYMS.items():\n" +
        "                if _field in valid and _norm in _syns:\n" +
        "                    _target = _field\n" +
        "                    break\n" +
        "            if _target is not None and _target not in out:\n" +
        "                out[_target] = out.pop(_key)\n" +
        "        return out\n" +
        "\n" +
        "    _sv_orig_action_from_arguments = _SvToolDef.action_from_arguments\n" +
        "\n" +
        "    def _sv_action_from_arguments(self, arguments):\n" +
        "        _args = arguments\n" +
        "        try:\n" +
        "            _args = _sv_remap_keys(self.action_type, arguments)\n" +
        "            try:\n" +
        "                from openhands.sdk.agent.utils import (\n" +
        "                    fix_malformed_tool_arguments as _sv_fix,\n" +
        "                )\n" +
        "                _args = _sv_fix(_args, self.action_type)\n" +
        "            except Exception:\n" +
        "                pass\n" +
        "        except Exception:\n" +
        "            _args = arguments\n" +
        "        return _sv_orig_action_from_arguments(self, _args)\n" +
        "\n" +
        "    _SvToolDef.action_from_arguments = _sv_action_from_arguments\n" +
        "except Exception:\n" +
        "    pass\n" +
        "\n" +
        "# Raise conservative upstream tool result caps for desktop use. The stock\n" +
        "# engine clips file_editor views at 16k chars and grep at 100 files, which\n" +
        "# makes local models claim they can only inspect tiny slices. Keep the limits\n" +
        "# bounded so weak models are not flooded, but high enough for real repository\n" +
        "# exploration.\n" +
        "try:\n" +
        "    import openhands.tools.file_editor.utils.constants as _sv_fe_consts\n" +
        "    import openhands.tools.file_editor.editor as _sv_fe_editor\n" +
        "    _sv_fe_consts.MAX_RESPONSE_LEN_CHAR = 64000\n" +
        "    _sv_fe_editor.MAX_RESPONSE_LEN_CHAR = 64000\n" +
        "except Exception:\n" +
        "    pass\n" +
        "\n" +
        "try:\n" +
        "    from openhands.tools.grep.impl import GrepExecutor as _SvGrepExecutorCaps\n" +
        "    _SvGrepExecutorCaps._MAX_MATCHES = 500\n" +
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
        "    pass\n" +
        "\n" +
        "# Raise glob's hardcoded 100-file cap to 500. The upstream implementation does\n" +
        "# not expose a constant, so patch the executor methods while preserving its\n" +
        "# sorting, ripgrep preference, and fallback behavior.\n" +
        "try:\n" +
        "    import glob as _sv_glob_module\n" +
        "    import os as _sv_os\n" +
        "    import subprocess as _sv_subprocess\n" +
        "    from pathlib import Path as _SvPath\n" +
        "    from openhands.sdk.utils import sanitized_env as _sv_sanitized_env\n" +
        "    from openhands.tools.glob.impl import GlobExecutor as _SvGlobExecutor\n" +
        "\n" +
        "    _SV_GLOB_MAX = 500\n" +
        "\n" +
        "    def _sv_glob_execute_with_ripgrep(self, pattern, search_path):\n" +
        "        search_path = search_path.resolve()\n" +
        "        _cmd = ['rg', '--files', str(search_path), '-g', pattern,\n" +
        "                '--sortr=modified']\n" +
        "        _result = _sv_subprocess.run(_cmd, capture_output=True, text=True,\n" +
        "                                    timeout=30, check=False,\n" +
        "                                    env=_sv_sanitized_env())\n" +
        "        _files = []\n" +
        "        if _result.stdout:\n" +
        "            for _line in _result.stdout.strip().split('\\n'):\n" +
        "                if _line:\n" +
        "                    _files.append(str(_SvPath(_line).resolve()))\n" +
        "                    if len(_files) >= _SV_GLOB_MAX:\n" +
        "                        break\n" +
        "        return _files, len(_files) >= _SV_GLOB_MAX\n" +
        "\n" +
        "    def _sv_glob_execute_with_glob(self, pattern, search_path):\n" +
        "        search_path = search_path.resolve()\n" +
        "        _original_cwd = _sv_os.getcwd()\n" +
        "        try:\n" +
        "            _sv_os.chdir(search_path)\n" +
        "            if '**' not in pattern:\n" +
        "                pattern = f'**/{pattern}'\n" +
        "            _matches = _sv_glob_module.glob(pattern, recursive=True)\n" +
        "            _paths = []\n" +
        "            for _match in _matches:\n" +
        "                _abs_path = str((search_path / _match).absolute())\n" +
        "                if _sv_os.path.isfile(_abs_path):\n" +
        "                    _paths.append((_abs_path, _sv_os.path.getmtime(_abs_path)))\n" +
        "            _paths.sort(key=lambda _x: _x[1], reverse=True)\n" +
        "            return [_path for _path, _ in _paths[:_SV_GLOB_MAX]], len(_paths) > _SV_GLOB_MAX\n" +
        "        finally:\n" +
        "            _sv_os.chdir(_original_cwd)\n" +
        "\n" +
        "    _SvGlobExecutor._execute_with_ripgrep = _sv_glob_execute_with_ripgrep\n" +
        "    _SvGlobExecutor._execute_with_glob = _sv_glob_execute_with_glob\n" +
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
        "    'openhands.tools.file_editor.definition',\n" +
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
