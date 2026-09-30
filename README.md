# CopyPilot

**The graphical cockpit for Microsoft AzCopy. Created by Arnold Kolkman.**

CopyPilot is a Windows desktop app (WPF, .NET 8) that puts a GUI on top of
[AzCopy v10](https://learn.microsoft.com/azure/storage/common/storage-use-azcopy-v10).
A recent AzCopy is bundled, so the MSI installs everything you need. No separate .NET runtime is required.

## Features

- **All AzCopy commands**: copy, sync, remove, list, make, set-properties, bench, login, login status, logout,
  jobs list/show/resume/remove/clean, and env.
- **All sources and destinations**:
  - local files, folders and UNC network shares
  - Azure Blob Storage
  - Azure Data Lake Storage Gen2
  - Azure Files (SMB and NFS)
  - Amazon S3
  - Google Cloud Storage
  - SFTP servers (SSH), see below
  - any URL you paste
  - pipes (stream a file through stdin/stdout)
- **All options**: over 100 AzCopy flags, grouped by category, searchable and filtered to the selected command.
  Each flag has a description and shows its default. Flags are tri-state: *on*, *off*, or *not set* (AzCopy's default).
  Anything not listed can go in *Additional arguments*.
- **Authentication and environment**: SAS tokens, Microsoft Entra ID login (device code, service principal,
  managed identity, Azure CLI, PowerShell, workload identity), and AWS and Google credentials. You can also set
  performance, logging and proxy variables. They are passed only to the AzCopy process CopyPilot starts.
- **Live command preview** with secrets masked. You can copy it, or export it as a `.ps1`/`.cmd` script (for example for Task Scheduler).
- **Live progress**: percentage, done/failed/skipped/pending, throughput and job id. The colored output log
  lets you answer AzCopy prompts, such as `--overwrite=prompt`.
- **Profiles**: save and load transfer definitions. Secrets are only saved if you choose to include them.
  The last session is restored on startup, without secrets.
- **AzCopy updates**: *AzCopy & tools → Download latest AzCopy* fetches the newest release from Microsoft.
  You can also point CopyPilot to your own `azcopy.exe`.
- **About page** crediting the creator, Arnold Kolkman.

## SFTP (SSH)

AzCopy has no SFTP support, so CopyPilot runs SFTP transfers itself using [SSH.NET](https://github.com/sshnet/SSH.NET).
Pick **SFTP server (SSH)** as the source or destination of **Copy** or **Sync**:

| Route | How it runs |
| --- | --- |
| Local ↔ SFTP, SFTP ↔ SFTP | Directly by CopyPilot |
| SFTP → Azure (Blob / ADLS Gen2 / Files) | CopyPilot downloads a batch into a local staging folder, AzCopy uploads it with all your options, and the batch is permanently deleted before the next one |
| Azure / S3 / GCS → SFTP | AzCopy downloads into a local staging folder; CopyPilot uploads each completed file right away and permanently deletes it |

- **Authentication:** password (also used for keyboard-interactive), private key (OpenSSH, PEM or PuTTY `.ppk`, with an optional passphrase), or both.
- **Host keys:** the server key is trusted on first use and stored in `%APPDATA%\CopyPilot\known_hosts`.
  After that, a changed key is refused. You can also pin a `SHA256:` fingerprint per location.
- **Supported options:** `--recursive`, `--as-subdir`, `--include-pattern`, `--exclude-pattern`, `--overwrite` (true/false/ifSourceNewer)
  and `--dry-run`. For Sync, files are compared by size and last-modified time, and `--delete-destination` is supported.
  Wildcards such as `/data/*.csv` also work. Last-modified times are preserved.
- **Small staging area:** every staged file is permanently deleted as soon as it has been transferred. The delete bypasses
  the Recycle Bin, like Shift+Delete. The staging size is limited by *sftp-staging-limit-mb* (default 256 MB):
  - SFTP → cloud uploads in batches up to that size.
  - Cloud → SFTP pauses AzCopy while completed files wait for the SFTP upload.
  - `0` means one file at a time. This uses the least space, but is slower for many small files.
  - A single file larger than the limit is still staged as a whole.
  - The job's staging folder is removed at the end, also when a job is cancelled or fails.
- **CopyPilot settings** in the *SFTP* group on the Options tab: the staging folder (default `%TEMP%\CopyPilot\staging`),
  the staging limit and the connection timeout.
- **Sync limitation:** when syncing SFTP → cloud, `--delete-destination` is not available, because files are uploaded in batches.
- Transfers that involve SFTP can't be exported as an AzCopy script. Save them as a profile instead.

## Install

Download `CopyPilot-<version>-x64.msi` from the GitHub Actions artifacts or a release and run it.
It installs to `C:\Program Files\CopyPilot` and adds Start menu and desktop shortcuts.
A portable ZIP is also produced.

## Build

Requirements: Windows, the .NET 8 SDK, and internet access (for the AzCopy download and the WiX NuGet packages).

```powershell
./build.ps1                 # latest AzCopy + tests + publish + MSI + portable ZIP
./build.ps1 -Version 1.2.0
```

Output goes to `artifacts\`. The GitHub workflow in `.github/workflows/build.yml` does the same on every push.
Pushing a tag `v1.2.3` creates a GitHub release with the MSI.

For development, open `CopyPilot.sln` in Visual Studio 2022. To run with AzCopy from the IDE, run `build.ps1`
once so that `build\azcopy\azcopy.exe` exists. You can also put `azcopy.exe` on your PATH.

## Project layout

| Path | Contents |
| --- | --- |
| `src/CopyPilot.Core` | AzCopy command/option catalog, URL builder, command builder, output parser, runner, profiles (cross-platform, unit-tested) |
| `src/CopyPilot` | WPF application (MVVM) |
| `src/CopyPilot.Core/Sftp` | SFTP engine: local/SFTP file trees, copy/sync engine, known-hosts store |
| `tests/CopyPilot.Core.Tests` | xUnit tests (SFTP integration tests run when `COPYPILOT_SFTP_TEST=host:port:user:password[:keyfile:passphrase]` is set) |
| `installer` | WiX v5 MSI project |
| `build.ps1` | End-to-end build script |

## Credits

- **CopyPilot**: © 2026 Arnold Kolkman.
- **AzCopy**: © Microsoft Corporation, MIT License. https://github.com/Azure/azure-storage-azcopy
- **SSH.NET** (SFTP): © the SSH.NET contributors, MIT License. https://github.com/sshnet/SSH.NET
