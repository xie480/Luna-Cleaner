# MemGuardian.Next

Windows 10/11 x64 `.NET 8` CLI for memory pressure diagnosis and conservative adaptive Working Set trim. Runtime code uses only .NET and documented Windows APIs; it does not make network requests or send telemetry.

## Build and test

Install the .NET 8 SDK x64, then from this directory run:

```powershell
dotnet build -c Release
dotnet test -c Release
```

The executable assembly name is `memguardian`. Build creates `src\MemGuardian.Next.Cli\bin\Release\net8.0\win-x64\memguardian.exe`; from source, use `dotnet run --project src/MemGuardian.Next.Cli -- <command>`. To create a publish directory:

```powershell
dotnet publish src/MemGuardian.Next.Cli -c Release -r win-x64 --self-contained false
```

Then invoke `src\MemGuardian.Next.Cli\bin\Release\net8.0\win-x64\publish\memguardian.exe status`.

## Commands

```text
memguardian status
memguardian top --by working-set
memguardian top --by commit
memguardian diagnose --duration 60
memguardian run
memguardian run --dry-run
memguardian once
```

`status`, `top` and `diagnose` never trim processes. `status` and `diagnose` persist bounded local trend/state samples; `top` only collects and ranks processes. `run` continuously monitors and may trim when Pressure/Critical has been confirmed. `once` waits for one confirmed pressure cycle and considers one recovery round. Begin with `run --dry-run` to review the exact selected and rejected candidates. `--dry-run` never calls `EmptyWorkingSet`.

## Measurement and safety model

- Physical RAM and Available RAM come from `GlobalMemoryStatusEx`; Commit, System Cache, Paged Pool and Nonpaged Pool come from `GetPerformanceInfo`.
- Process Working Set and Private Commit come from `GetProcessMemoryInfo(PROCESS_MEMORY_COUNTERS_EX)`. System Working Set is not claimed by summing these values because shared resident pages may be counted more than once.
- Page Reads/sec, Pages Input/sec and pagefile usage are independent optional `PdhAddEnglishCounterW` counters. An unavailable counter displays `n/a`; a trim with unverified paging enters Backoff.
- Reclaim candidates must be current-user/current-Session, non-foreground, observed idle for at least 10 minutes, below configured CPU/I/O rates, at least 256 MiB Working Set, outside denylist and cooldown, and have verified user/process identity. Unknown recent-use history is excluded.
- One round handles at most two processes, sampling after each process. A negative result stops the next trim. Global cooldown is at least 5 minutes and per-process cooldown at least 15 minutes.
- `EmptyWorkingSet` can make resident pages reclaimable; it does not guarantee release of Private Commit. Feedback reports Working Set, Available RAM, system Commit and paging separately.

## Local state

User-editable settings, bounded trends, process-use history, cooldowns and feedback scores live under `%LOCALAPPDATA%\MemGuardian.Next`. Settings are clamped so a JSON edit cannot lower the 120-second recent-use guard, 10-minute idle/leak window, pressure confirmation, 5/15-minute cooldowns, 256 MiB candidate floor, 64 MiB success measure, minimum strategy score, or two-process round limit. Required Windows-process denylist entries cannot be removed through settings.

## Performance verification

The automated tests validate state and policy decisions with synthetic snapshots; they do not prove that a trim reduces real application stalls. On a representative Windows 10/11 x64 machine, compare matched workload runs with trimming disabled and enabled. Capture Available RAM, Commit, Working Set, Page Reads/sec, Pages Input/sec, target app response-time percentiles, and system hard-fault rate for several minutes before and after each action. Stop if paging or response time worsens, and keep a no-trim baseline. Do not use post-trim Working Set alone as a success measure.
