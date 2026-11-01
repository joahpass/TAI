# Desktop workspace entry adaptation

Production source changes are limited to `../Program.cs` and `../MainForm.cs`.

- `HostModes.StartUrl(mode, embedded = false)` sends standalone Assistant to `/workspace`, embedded hosts to `/assistant`, and Console to `/`.
- `MainForm.StartAsync` passes the actual embedded state to the URL selector.
- Standalone Assistant uses the existing 1200×820 logical console/workspace sizing and 720×520 minimum, including monitor DPI scaling and work-area limits. It centers on the owner's monitor when an owner exists.
- Embedded construction keeps the original 520×860 compact initial size. `ConfigureEmbedded` still clears the minimum; `EmbeddedSession` still controls the actual panel width (440 logical default, 320 minimum, maximum 60% of TIA client width), layout, ownership/parenting and hiding.
- `OnNewWindowRequested` handles a user-initiated local `/workspace` popup from an embedded panel by passing the original `e.Uri` directly to `OpenExternal`. Design query strings and fragments remain intact. The panel never navigates in that branch. Non-user-initiated workspace popups are logged and blocked.
- User-initiated `/` and `/settings` configuration popups start or activate the existing Console in Assistant mode. Console continues navigating configuration popups in place. Console startup remains `/`.
- TIA owner resolution, top-level ownership, WS_CHILD parenting, DPI-context matching, window markers, mutexes, floating ball, resize and hide handlers are unchanged.

## Verification performed

The existing .NET SDK is 9.0.311 (Roslyn C# 4.14). Standard offline build was attempted:

```bash
dotnet build TAI.Desktop.csproj --no-restore -c Release -p:OutputPath=checks/build/ -p:IntermediateOutputPath=checks/obj/ -p:NuGetAudit=false
```

It returned `NETSDK1064` because `Microsoft.Web.WebView2` version `1.0.3537.50` in the old NuGet assets file is absent from the package cache. No package restore or network installation was attempted.

Compilation then succeeded using the installed Roslyn compiler, existing Windows .NET Framework assemblies and existing WebView2 DLLs from `bin/Release`. All seven production desktop source files compiled successfully with C# 7.3 and x64 target. These response files deliberately use installed local paths, rather than requesting dependencies:

```bash
dotnet 'C:/Program Files/dotnet/sdk/9.0.311/Roslyn/bincore/csc.dll' @checks/desktop-build.rsp
dotnet 'C:/Program Files/dotnet/sdk/9.0.311/Roslyn/bincore/csc.dll' @checks/desktop-tests.rsp
./checks/build/DesktopEntryChecks.exe
```

Run commands from the desktop directory; `checks/build` holds isolated check artifacts. These compile outputs are verification binaries, not deployment packages (the ad-hoc compiler invocation does not package the application manifest or dependencies).

Result: **PASS: 22 desktop entry checks**. Checks cover the three start URLs, assistant/embedded argument parsing and retained owner PID, workspace URL query/fragment recognition, `/settings` and legacy `/` routes, exact path and allowed-origin boundaries, initial workspace size and centering, minimum size constants, scaled small-monitor containment, and retained compact constants.

The test source is wrapped in `#if DESKTOP_ENTRY_TESTS`, so ordinary SDK compilation does not acquire an extra entry point. The test executable does not instantiate MainForm, initialize WebView2, start the local server, open a browser or interact with TIA. External browser launching and embedded WS_CHILD behavior were reviewed in source; they were not exercised against a live TIA process. Installed TAI versions were not changed. No git commit was created.
