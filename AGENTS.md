# Project Guidelines

## Communication
- Always reply to the user in Simplified Chinese.
- Keep explanations easy to read: short sentences, clear steps, avoid unnecessary jargon.
- When listing commands, prefer Windows PowerShell style paths and examples.

## Build And Run
- Prerequisites:
  - Windows 11 22H2+ (recommended and validated target for BLE runtime)
  - Windows 10 22H2 can build, but BLE behavior is not guaranteed
  - ViGEmBus installed
  - .NET 9 SDK/runtime available
- Restore/build solution:
  - `dotnet restore joycon2.sln`
  - `dotnet build joycon2.sln -c Debug`
- Run desktop app:
  - `dotnet run --project src/Joycon2PC.App/Joycon2PC.App.csproj`
- Release publish (framework-dependent preferred for smaller size):
  - `dotnet publish src/Joycon2PC.App/Joycon2PC.App.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish/`

## Architecture
- `src/Joycon2PC.App`: WinForms UI and BLE device lifecycle (scan/connect/reconnect/notifications).
- `src/Joycon2PC.Core`: Joy-Con 2 protocol parsing and subcommand building.
- `src/Joycon2PC.ViGEm`: Mapping Joy-Con state to virtual Xbox 360 controller via ViGEm.
- Dependency direction: App -> Core, ViGEm; ViGEm -> Core.

## Project Conventions
- Treat this as a Windows-only desktop project; avoid proposing cross-platform runtime behavior unless explicitly requested.
- Keep protocol logic in Core and transport/UI logic in App; avoid mixing parser code into WinForms handlers.
- Preserve existing Joy-Con 2 report assumptions:
  - Input report size is 63 bytes.
  - Button bits are read from bytes `[4..7]`.
  - Sticks are packed 12-bit fields from bytes `[10..15]`.
- Respect current conditional compilation in BLE code (for example `INTHEHAND` related blocks) unless task requires changing it.

## Pitfalls
- Solution uses mixed target frameworks (`net9.0-windows10.0.22621.0` for App, `net7.0` for Core/ViGEm). Do not "normalize" framework versions unless requested.
- Bluetooth behavior depends on Windows BLE stack and real hardware state; avoid claiming reconnect or rumble fixes without validating flow.
- There is currently no automated test project in this repository. If behavior changes, prefer adding focused validation steps and explicit manual verification notes.

## Key Reference Files
- `README.md`
- `src/Joycon2PC.App/Bluetooth/BLEScanner.cs`
- `src/Joycon2PC.Core/JoyconParser.cs`
- `src/Joycon2PC.Core/JoyconButton.cs`
- `src/Joycon2PC.ViGEm/ViGEmBridge.cs`

## /init-deep Command (Improved for Small Projects)

Generate hierarchical AGENTS.md files with optimizations for project size.

### Usage

```
/init-deep                      # Update mode: modify existing + create new where warranted
/init-deep --create-new         # Read existing → remove all → regenerate from scratch
/init-deep --max-depth=2        # Limit directory depth (default: calculated based on size)
```

### Improvements for Small Projects

1. **Early Exit**: Projects with <20 files skip AGENTS.md generation entirely
2. **Dynamic Depth**: Max depth calculated automatically:
   - <50 files: max-depth=1
   - 50-200 files: max-depth=2  
   - >200 files: max-depth=3
3. **Simplified Scoring**: For <50 files, uses only file/subdir counts
4. **Smart Subdir Generation**: Only creates subdirectory AGENTS.md if:
   - Subdirectory has >5 files AND
   - Has unique conventions (detected via exploration)

### Workflow (High-Level)

1. **Discovery + Analysis** (concurrent)
   - Fire background explore agents immediately
   - Main session: PowerShell structure + LSP codemap + read existing AGENTS.md
2. **Size Check** - Skip generation for very small projects (<20 files)
3. **Score & Decide** - Determine AGENTS.md locations from merged findings (simplified for small projects)
4. **Generate** - Root first, then subdirs in parallel (with smart filtering)
5. **Review** - Deduplicate, trim, validate

**TodoWrite ALL phases. Mark in_progress → completed in real-time.**

#### Phase 1: Discovery + Analysis (Concurrent)

**Mark "discovery" as in_progress.**

##### Fire Background Explore Agents IMMEDIATELY

Don't wait—these run async while main session works.

```
// Fire all at once, collect results later
task(subagent_type="explore", load_skills=[], description="Explore project structure", run_in_background=true, prompt="Project structure: PREDICT standard patterns for detected language → REPORT deviations only")
task(subagent_type="explore", load_skills=[], description="Find entry points", run_in_background=true, prompt="Entry points: FIND main files → REPORT non-standard organization")
task(subagent_type="explore", load_skills=[], description="Find conventions", run_in_background=true, prompt="Conventions: FIND config files (.eslintrc, pyproject.toml, .editorconfig) → REPORT project-specific rules")
task(subagent_type="explore", load_skills=[], description="Find anti-patterns", run_in_background=true, prompt="Anti-patterns: FIND 'DO NOT', 'NEVER', 'ALWAYS', 'DEPRECATED' comments → LIST forbidden patterns")
task(subagent_type="explore", load_skills=[], description="Explore build/CI", run_in_background=true, prompt="Build/CI: FIND .github/workflows, Makefile → REPORT non-standard patterns")
task(subagent_type="explore", load_skills=[], description="Find test patterns", run_in_background=true, prompt="Test patterns: FIND test configs, test structure → REPORT unique conventions")
```

<dynamic-agents>
**DYNAMIC AGENT SPAWNING**: After PowerShell analysis, spawn ADDITIONAL explore agents based on project scale:

| Factor | Threshold | Additional Agents |
|--------|-----------|-------------------|
| **Total files** | >100 | +1 per 100 files |
| **Total lines** | >10k | +1 per 10k lines |
| **Directory depth** | ≥4 | +2 for deep exploration |
| **Large files (>500 lines)** | >10 files | +1 for complexity hotspots |
| **Monorepo** | detected | +1 per package/workspace |
| **Multiple languages** | >1 | +1 per language |

```powershell
# Measure project scale first
$exclude = @("node_modules",".git","bin","obj","dist","build")
$allFiles = Get-ChildItem -Recurse -File -Force -Exclude $exclude
$total_files = $allFiles.Count
$total_lines = ($allFiles | Where-Object { $_.Extension -match '\.(ts|py|go|js|rs|cs)$' } | Get-Content | Measure-Object -Line).Lines
$large_files = ($allFiles | Where-Object { $_.Length -gt 500 }).Count
$max_depth = ($allFiles | ForEach-Object { $_.FullName.Split([IO.Path]::DirectorySeparatorChar).Count } | Measure-Object -Maximum).Maximum
```

Example spawning:
```
// 500 files, 50k lines, depth 6, 15 large files → spawn 5+5+2+1 = 13 additional agents
task(subagent_type="explore", load_skills=[], description="Analyze large files", run_in_background=true, prompt="Large file analysis: FIND files >500 lines, REPORT complexity hotspots")
task(subagent_type="explore", load_skills=[], description="Explore deep modules", run_in_background=true, prompt="Deep modules at depth 4+: FIND hidden patterns, internal conventions")
task(subagent_type="explore", load_skills=[], description="Find shared utilities", run_in_background=true, prompt="Cross-cutting concerns: FIND shared utilities across directories")
// ... more based on calculation
```
</dynamic-agents>

##### Main Session: Concurrent Analysis

**While background agents run**, main session does:

###### 1. PowerShell Structural Analysis
```powershell
# Directory depth + file counts
$exclude = @("node_modules",".git","venv","dist","build")
Get-ChildItem -Recurse -Directory -Force -Exclude $exclude | ForEach-Object { 
    $_.FullName.Split([IO.Path]::DirectorySeparatorChar).Count 
} | Group-Object | Sort-Object Name

# Files per directory (top 30)
Get-ChildItem -Recurse -File -Force -Exclude $exclude | Group-Object DirectoryName | 
    Sort-Object Count -Descending | Select-Object -First 30

# Code concentration by extension
$codeExts = @("*.py","*.ts","*.tsx","*.js","*.go","*.rs","*.cs")
Get-ChildItem -Recurse -File -Force -Include $codeExts -Exclude $exclude | 
    Group-Object DirectoryName | Sort-Object Count -Descending | Select-Object -First 20

# Existing AGENTS.md / CLAUDE.md
Get-ChildItem -Recurse -File -Force -Include "AGENTS.md","CLAUDE.md" -Exclude $exclude
```

###### 2. Read Existing AGENTS.md
```
For each existing file found:
  Read(filePath=file)
  Extract: key insights, conventions, anti-patterns
  Store in EXISTING_AGENTS map
```

If `--create-new`: Read all existing first (preserve context) → then delete all → regenerate.

###### 3. LSP Codemap (if available)
```
LspServers()  # Check availability

# Entry points (parallel)
LspDocumentSymbols(filePath="src/index.ts")
LspDocumentSymbols(filePath="main.py")

# Key symbols (parallel)
LspWorkspaceSymbols(filePath=".", query="class")
LspWorkspaceSymbols(filePath=".", query="interface")
LspWorkspaceSymbols(filePath=".", query="function")

# Centrality for top exports
LspFindReferences(filePath="...", line=X, character=Y)
```

**LSP Fallback**: If unavailable, rely on explore agents + AST-grep.

##### Collect Background Results

```
// After main session analysis done, collect all task results
for each task_id: background_output(task_id="...")
```

**Merge: PowerShell + LSP + existing + explore findings. Mark "discovery" as completed.**

#### Phase 2: Scoring & Location Decision

**Mark "scoring" as in_progress.**

##### Early Exit for Small Projects
```powershell
$exclude = @("node_modules",".git")
$total_files = (Get-ChildItem -Recurse -File -Force -Exclude $exclude).Count
if ($total_files -lt 20) {
  Write-Host "Project too small ($total_files files), skipping AGENTS.md generation"
  exit 0
}
```

##### Scoring Matrix (Adaptive by Size)

For projects <50 files (simplified):
| Factor | Weight | High Threshold | Source |
|--------|--------|----------------|--------|
| File count | 2x | >10 | powershell |
| Subdir count | 2x | >3 | powershell |

For projects ≥50 files (full):
| Factor | Weight | High Threshold | Source |
|--------|--------|----------------|--------|
| File count | 3x | >20 | powershell |
| Subdir count | 2x | >5 | powershell |
| Code ratio | 2x | >70% | powershell |
| Unique patterns | 1x | Has own config | explore |
| Module boundary | 2x | Has index.ts/__init__.py | powershell |
| Symbol density | 2x | >30 symbols | LSP |
| Export count | 2x | >10 exports | LSP |
| Reference centrality | 3x | >20 refs | LSP |

##### Decision Rules

| Score | Action |
|-------|--------|
| **Root (.)** | ALWAYS create |
| **>15** | Create AGENTS.md |
| **8-15** | Create if distinct domain |
| **<8** | Skip (parent covers) |

**Output:**
```
AGENTS_LOCATIONS = [
  { path: ".", type: "root" }
  // + subdirs that pass filters
]
```

**Mark "scoring" as completed.**

#### Phase 3: Generate AGENTS.md

**Mark "generate" as in_progress.**

<critical>
**File Writing Rule**: If AGENTS.md already exists at the target path → use `Edit` tool. If it does NOT exist → use `Write` tool.
NEVER use Write to overwrite an existing file. ALWAYS check existence first via `Read` or discovery results.
</critical>

##### Root AGENTS.md (Full Treatment)

```markdown
# PROJECT KNOWLEDGE BASE

**Generated:** {TIMESTAMP}
**Commit:** {SHORT_SHA}
**Branch:** {BRANCH}

## OVERVIEW
{1-2 sentences: what + core stack}

## STRUCTURE
\`\`\`
{root}/
├── {dir}/    # {non-obvious purpose only}
└── {entry}
\`\`\`

## WHERE TO LOOK
| Task | Location | Notes |
|------|----------|-------|

## CODE MAP
{From LSP - skip if unavailable or project <10 files}

| Symbol | Type | Location | Refs | Role |
|--------|------|----------|------|------|

## CONVENTIONS
{ONLY deviations from standard}

## ANTI-PATTERNS (THIS PROJECT)
{Explicitly forbidden here}

## UNIQUE STYLES
{Project-specific}

## COMMANDS
```powershell
{dev/test/build}
```

## NOTES
{Gotchas}
```

**Quality gates**: 50-150 lines, no generic advice, no obvious info.

##### Subdirectory AGENTS.md (Parallel with Smart Filtering)

For each potential location from scoring:
```powershell
# Skip if: <5 files OR no unique conventions detected
if ($subdir_files -lt 5 -or $unique_conventions -eq $false) {
  continue
}
```

```
for loc in AGENTS_LOCATIONS (except root):
  task(category="writing", load_skills=[], run_in_background=false, description="Generate AGENTS.md", prompt=\`
    Generate AGENTS.md for: ${loc.path}
    - Reason: ${loc.reason}
    - 30-80 lines max
    - NEVER repeat parent content
    - Sections: OVERVIEW (1 line), STRUCTURE (if >5 subdirs), WHERE TO LOOK, CONVENTIONS (if different), ANTI-PATTERNS
  \`)
```

**Wait for all. Mark "generate" as completed.**

#### Phase 4: Review & Deduplicate

**Mark "review" as in_progress.**

For each generated file:
- Remove generic advice
- Remove parent duplicates
- Trim to size limits
- Verify telegraphic style

**Mark "review" as completed.**

#### Final Report

```
=== init-deep Complete ===

Mode: {update | create-new}

Files:
  [OK] ./AGENTS.md (root, {N} lines)
  [OK] ./src/hooks/AGENTS.md ({N} lines)

Dirs Analyzed: {N}
AGENTS.md Created: {N}
AGENTS.md Updated: {N}

Hierarchy:
  ./AGENTS.md
  └── src/hooks/AGENTS.md
```

#### Anti-Patterns

- **Static agent count**: MUST vary agents based on project size/depth
- **Sequential execution**: MUST parallel (explore + LSP concurrent)
- **Ignoring existing**: ALWAYS read existing first, even with --create-new
- **Over-documenting**: Not every dir needs AGENTS.md
- **Redundancy**: Child never repeats parent
- **Generic content**: Remove anything that applies to ALL projects
- **Verbose style**: Telegraphic or die