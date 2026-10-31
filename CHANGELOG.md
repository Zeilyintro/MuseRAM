# Changelog / 变更日志

All notable changes to MuseRAM are listed here, newest first. Each entry has an English line followed by the original Chinese release note.
以下按时间倒序列出 MuseRAM 的重要变更,每条先给英文,再给中文。完整说明见 [Releases](https://github.com/Zeilyintro/MuseRAM/releases)。

## [0.1.7.11] - 2026-10-12 — Performance / 性能优化

- Lighter on its own resources: in a local sampler benchmark, per-sample time dropped by about 76% and temporary allocations by about 93%. Figures are averages of 20 samples per group and vary by device and load; fewer temporary allocations do not mean resident memory or system usage fall by the same proportion.
- Quieter in the background: while hidden or minimized, the spinner animation and benefit-analysis table stop refreshing. Background sampling and learning continue, and the latest data appears when the window is reopened.
- Fixed: the Optimize Now button is no longer disabled by mistake while checking for updates.
- 本次让 MuseRAM 自身更轻:本机独立采样器对照测试中,单次采样耗时约降低 76%,临时内存分配约减少 93%(每组 20 次采样平均值,实际表现因设备与负载而异)。
- 隐藏或最小化后,转圈动画与收益分析表暂停刷新,后台采样和学习照常运行。
- 修复:检查更新时,“立即优化”按钮不再被误禁用。

## [0.1.7.10] - 2026-09-28 — Fix / 修复版

- Fixed: on some Windows input-device setups, the process stayed in the background after exiting from the system tray.
- 修复:部分 Windows 输入设备环境下,从系统托盘退出后程序进程仍留在后台的问题。

## [0.1.7.9] - 2026-09-04

- Minor maintenance update.
- 小幅维护更新。

## [0.1.7.8] - 2026-08-15

- Clearer wording and status messages.
- 优化文案与状态提示,让信息更清楚、更好理解。

## [0.1.7.7] - 2026-08-14

- Improved steady-state sample review and progress display; smoother scheduled operations and interface details.
- 优化稳态样本复核与进度展示,定时操作和界面细节也更顺手。

## [0.1.7.6] - 2026-08-13

- Added Global Reclaim: frees more background memory quickly, but may cause more noticeable memory rebound or reloading later.
- Fixed some strategy issues and refined usage details.
- 新增全域回收:快速释放更多后台内存,但可能带来更明显的回弹或重新加载。
- 修复部分策略问题,并优化了一些使用细节。

## [0.1.7.5] - 2026-08-12

- Fixed a crash in the protection list.
- 修复了保护列表中的闪退问题。

## [0.1.7.4] - 2026-08-12

- Refined usage details.
- 优化了一些使用细节,让 MuseRAM 更顺手。

## [0.1.7.3] - 2026-08-11

- Improved the steady-state strategy.
- 优化稳态策略。

## [0.1.7.2] - 2026-08-11

- Improved the interface presentation.
- 优化 UI 界面展示。

## [0.1.7.1] - 2026-08-11

- Fixed: the "minimized, observing" state now shows the same gray eye icon as regular observation.
- This release also verified the full automatic-update flow from 0.1.7 to 0.1.7.1.
- 修复:“最小化观察中”现在显示与普通活动观察一致的灰色眼睛图标。
- 本版本同时用于验证 MuseRAM 从 0.1.7 自动更新至 0.1.7.1 的完整流程。

## [0.1.7] - 2026-08-11 — First public beta / 首个公开测试版本

- Memory overview, process and candidate lists.
- Manual and automatic optimization, application protection, and benefit learning.
- Custom profiles, light and dark themes, Chinese and English interface.
- Built-in update check with safe replacement flow.
- Requires Windows 10/11 x64 and the .NET 8 Desktop Runtime.
- 提供内存概览、进程与候选列表。
- 支持手动与自动优化、应用保护和收益学习。
- 支持自定义档位、明暗主题与中英文界面。
- 内置更新检查与安全替换流程。
- 运行要求:Windows 10/11 x64、.NET 8 Desktop Runtime。

[0.1.7.11]: https://github.com/Zeilyintro/MuseRAM/releases/tag/v0.1.7.11
[0.1.7.10]: https://github.com/Zeilyintro/MuseRAM/releases/tag/v0.1.7.10
[0.1.7.9]: https://github.com/Zeilyintro/MuseRAM/releases/tag/v0.1.7.9
[0.1.7.8]: https://github.com/Zeilyintro/MuseRAM/releases/tag/v0.1.7.8
[0.1.7.7]: https://github.com/Zeilyintro/MuseRAM/releases/tag/v0.1.7.7
[0.1.7.6]: https://github.com/Zeilyintro/MuseRAM/releases/tag/v0.1.7.6
[0.1.7.5]: https://github.com/Zeilyintro/MuseRAM/releases/tag/v0.1.7.5
[0.1.7.4]: https://github.com/Zeilyintro/MuseRAM/releases/tag/v0.1.7.4
[0.1.7.3]: https://github.com/Zeilyintro/MuseRAM/releases/tag/v0.1.7.3
[0.1.7.2]: https://github.com/Zeilyintro/MuseRAM/releases/tag/v0.1.7.2
[0.1.7.1]: https://github.com/Zeilyintro/MuseRAM/releases/tag/v0.1.7.1
[0.1.7]: https://github.com/Zeilyintro/MuseRAM/releases/tag/v0.1.7
