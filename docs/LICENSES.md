# Licenses and dependency audit

## Project and migration source

SIGNALFORGE is licensed under GNU GPL-3.0-or-later. The Terminal2 migration is based on Michael Heyns's GPL-3.0-or-later project. Copyright notices are retained in the compatibility source files; the complete license is in the root `LICENSE`. The pinned upstream checkout is `external/Terminal2` at `d3adbd301963b729885cc41fb75b3a80bcdf5472`.

## Direct dependencies

| Package | Resolved version | License | Use |
|---|---:|---|---|
| Avalonia and Avalonia.Desktop / Fluent / Inter | 12.1.4 | MIT | Cross-platform UI and styling |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | MVVM source generators and commands |
| Dock.Avalonia, Dock.Avalonia.Themes.Fluent, Dock.Model.Mvvm, Dock.Serializer.SystemTextJson | 12.1.0.12 | MIT | Dockable explorer, inspector and timeline panes; nested workspace splits, floating windows and saved layouts |
| Microsoft.Extensions.DependencyInjection | 10.0.13 | MIT | Desktop composition |
| RoyalApps.RoyalTerminal.Avalonia | 0.6.3 | MIT | Reused VT terminal control and local PTY; not SIGNALFORGE's binary protocol source |
| System.IO.Ports | 10.0.12 | MIT | Serial transport API |

## Transitive dependency audit

Audited 2026-10-23 from the restored `packages.lock.json` files and NuGet package `.nuspec` metadata for the `net10.0` desktop target. `dotnet list SignalForge.slnx package --vulnerable --include-transitive` reported no vulnerable packages from the configured NuGet source on this date. Re-run this audit before publishing because dependency metadata and advisories change.

| Dependency group | Resolved packages | License / notice |
|---|---|---|
| Avalonia platform, rendering, and build components | Avalonia 12.1.4 modules; Avalonia.BuildServices 11.3.2 | MIT; Avalonia copyright notices below |
| Dock layout components | Dock.Avalonia, Dock.Model.Mvvm, Dock.Serializer.SystemTextJson, Dock.Controls, Dock.Model and Dock.Settings 12.1.0.12 | MIT; Wiesław Šoltés notice below |
| RoyalTerminal 0.6.3 modules | Terminal, VT, PTY (Unix and Windows), serial, SSH abstractions, SSH.NET adapter, Telnet, rendering, shaders, Sixel, Unicode, and services | MIT; Royal Apps Contributors notice below |
| Terminal and rendering dependencies | SSH.NET 2026.0.0; BouncyCastle.Cryptography 2.7.0; Pretext 0.1.0 family; SkiaSharp 3.119.4 family; HarfBuzzSharp 8.3.1.5 family; MicroCom.Runtime 0.11.6 | MIT; copyright notices below |
| .NET packages | Microsoft.Extensions.* 10.0.13 / 8.0.3; System.IO.Ports 10.0.12 and its runtime assets; ProtectedData 10.0.12; RecyclableMemoryStream 3.0.1 | MIT; Microsoft / .NET Foundation notices below |
| Tmds.DBus.Protocol | 0.94.1 | MIT; Tom Deseyn notice below |
| Avalonia.Angle.Windows.Natives | 2.1.27548.20260419 | Includes its own ANGLE license at [ANGLE.txt](../licenses/ANGLE.txt) |

The desktop dependency graph includes RoyalTerminal's SSH and Telnet implementation packages through the selected Avalonia package. SIGNALFORGE uses them in a separate remote terminal surface alongside the local PTY and engineering byte transports. No credentials are preconfigured or persisted; SSH passwords are held in process memory for the session. SSH key/agent authentication is not implemented. `AvaloniaUI.DiagnosticsSupport` was removed because its NuGet metadata did not declare a license and the product does not need its developer-tools bridge.

## MIT copyright notices

The following notices and the MIT permission terms apply to the corresponding resolved NuGet packages:

- Copyright 2013-2026 The AvaloniaUI Project (Avalonia modules, version 12.1.4).
- Copyright 2023-2025 The AvaloniaUI Project (Avalonia.BuildServices 11.3.2).
- Copyright © Wiesław Šoltés 2026 (Dock 12.1.0.12 packages).
- Copyright © 2026 Royal Apps Contributors (RoyalTerminal 0.6.3 modules).
- Copyright © Microsoft Corporation; (c) .NET Foundation and Contributors (Microsoft.Extensions.*, System.IO.Ports, ProtectedData, and their runtime assets).
- Copyright 2021 © Nikita Tsukanov (MicroCom.Runtime 0.11.6).
- Copyright © Legion of the Bouncy Castle Inc. 2000-2026 (BouncyCastle.Cryptography 2.7.0).
- Copyright © Renci 2010-2026 (SSH.NET 2026.0.0).
- Copyright (c) Wieslaw Soltes (Pretext 0.1.0 family).
- Copyright © Microsoft Corporation (SkiaSharp 3.119.4 and HarfBuzzSharp 8.3.1.5 packages and native assets).
- Copyright (c) Tom Deseyn (Tmds.DBus.Protocol 0.94.1).

MIT permission notice:

> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

The full ANGLE license text is retained separately at [ANGLE.txt](../licenses/ANGLE.txt). This register is based on the current NuGet restore graph, not a guarantee that a future release has an unchanged dependency set. Refresh the lock files, license notices, vulnerability audit, and packaged-file inventory for every release.
