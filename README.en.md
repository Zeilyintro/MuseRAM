# MuseRAM

[简体中文](./README.md) | [English](./README.en.md)

MuseRAM is a memory management tool for Windows. It quietly observes system activity, helps identify background applications suitable for optimization, and reduces memory usage while minimizing disruption to your work.

> Current version: `0.1.7.3` · Beta

![MuseRAM overview and candidate list](./docs/images/overview-dark.png)

## Main Features

- **Clear memory overview**: View memory usage, recent changes, optimization results, and MuseRAM's own resource usage in one place.
- **Manual or automatic optimization**: Optimize immediately or let MuseRAM act automatically according to the current system state.
- **Transparent candidate decisions**: See which applications are eligible for optimization and why others are temporarily deferred.
- **Application protection**: Protect an entire application or only selected executables within it.
- **Benefit learning**: Learn from previous optimization results to make future decisions better suited to real usage.
- **Multiple profiles**: Choose Lite, Turbo, or Ultimate, or adjust a custom profile to suit your needs.
- **Light and dark themes with Chinese and English interfaces**: Follow the Windows theme or switch manually.

## Interface Preview

### Running Processes

View running applications, their current status, and whether they are protected or retained.

![MuseRAM running processes](./docs/images/processes.png)

### Application Protection

Protect an entire application or selected executables, and expand an entry to view related processes.

![MuseRAM application protection](./docs/images/protection.png)

### Benefit Learning

Review historical optimization results, stable memory release, and sample progress for each application.

![MuseRAM benefit learning](./docs/images/benefit-learning.png)

### Custom Profile

Adjust optimization scope, waiting periods, and other preferences beyond the built-in profiles.

![MuseRAM custom profile](./docs/images/custom-profile.png)

## Download and Use

1. Download the latest `MuseRAM.exe` from [GitHub Releases](https://github.com/Zeilyintro/MuseRAM/releases).
2. Place the file in a permanent folder and run it.
3. Grant administrator permission when prompted by Windows.
4. For your first use, keep the default settings and protect important applications used for work, creative tasks, or games.

Requirements:

- 64-bit Windows 10 or Windows 11
- 64-bit .NET 8 Desktop Runtime
- Administrator permission when performing memory optimization

## Usage Notes

Regular memory optimization does not close applications. However, an application may reload content or respond briefly when that content is used again. Protect applications that are editing documents, creating content, or running important tasks.

Deep Release is different from regular optimization. It is intended for background applications you have confirmed are no longer needed. Check for unsaved work before using it.

## Local Data and Privacy

MuseRAM stores settings, protection rules, history, and learning data locally. Diagnostic data collection is disabled by default. When enabled manually, diagnostics are written only to local files and are never uploaded automatically.

## Current Status

MuseRAM is still in beta. The interface, default settings, and some behavior may continue to change. Start with the default profiles and report reproducible problems through [GitHub Issues](https://github.com/Zeilyintro/MuseRAM/issues).

When reporting an issue, include the MuseRAM version, Windows version, steps leading to the problem, and relevant screenshots when possible. Remove usernames, file paths, and other private information before sharing screenshots or logs.
