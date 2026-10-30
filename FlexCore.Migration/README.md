# FlexCore.Migration

Migration tooling built on FlexCore, for code-migration hosts: the converter shell (`Fx.ControlKit.Conversion`),
live program preview (`Fx.ControlKit.Preview`), translated-artifact viewers (`Fx.ControlKit.Artifacts`) and the
Crystal samples pack (`Reports/CrystalSamples/CrystalSamples.db`, read through `CrystalSampleDatabase`).

It is a satellite of FlexCore and is never packed: FlexCore's NuGet package carries neither this code, its
`Microsoft.Data.Sqlite` dependency nor the samples database. Reference it next to FlexCore:

```xml
<ProjectReference Include="..\FlexCore\FlexCore.csproj" />
<ProjectReference Include="..\FlexCore\FlexCore.Migration\FlexCore.Migration.csproj" />
```

Its scripts are served as `_content/FlexCore.Migration/converter-shell.js` and
`_content/FlexCore.Migration/program-preview.js`. Namespaces are unchanged from when the tooling lived in FlexCore.

## Live program preview

`ProgramPreviewWindow`, `ProgramPreviewPair`, and `ProgramPreviewGroup` run programs on the server and show them in the browser. A pane is a GUI window, a console, or a web app. Each pane has a frame and a title bar; `Chrome` can be the default card, a classic Windows frame, a Qt Fusion frame, a browser window, or none. Clicks and keystrokes in a GUI preview are delivered to that process. A web pane starts a server and waits until its URL responds, or shows an address that is already hosted. The page iframes it and can open the same URL in a separate tab. `ConverterShell` can show a pair as a Run / Preview mode when the host passes `EnableRunPreview`, the launch commands, and `OwnerKey` for the signed-in user.

The host registers `AddFlexCoreProgramPreview` and, for image delivery outside the circuit, maps `ProgramPreviewHttp.DefaultRoutePrefix`. A launched web app also needs the `{prefix}/{sessionId}/app` proxy route. GUI sessions need Xvfb, xdotool, and ffmpeg (or xwd) on the server. When those tools are missing, the preview stays on the page and names the packages to install. Console and web programs can be relayed without a virtual display.
