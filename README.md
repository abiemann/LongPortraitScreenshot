# Long Portrait Screenshot

A Windows desktop utility that selects a vertically scrollable UI Automation element, captures each visible viewport, and stitches the captures into one PNG.

## Requirements

- Windows 10 or Windows 11
- Visual Studio 2026 with the .NET desktop development workload, or the .NET 10 SDK

## Build

Open `LongPortraitScreenshot.sln` in Visual Studio and build the solution, or run:

```powershell
dotnet build .\LongPortraitScreenshot.sln
```

To run the dependency-free self-tests:

```powershell
dotnet run --project .\tests\LongPortraitScreenshot.SelfTest\LongPortraitScreenshot.SelfTest.csproj
```

## Use

1. Open the window containing the scrollable content and leave it unobscured.
2. Drag the finder target over the scrollable pane. A green border identifies the selected container.
3. Release the mouse. The utility scrolls the pane, captures it, restores its original position, and asks where to save the PNG.
4. Press Escape during capture to cancel.

The scrollbar-crop and horizontal-empty-space trim options are enabled by default and remembered for each Windows user. Horizontal trimming keeps a 5-pixel margin beside detected content.

The first release supports vertical UI Automation scroll containers. Elevated, protected, minimized, moving, dynamically changing, or non-UI-Automation controls may not be capturable.

## Project structure

```text
LongPortraitScreenshot.sln
src/
  LongPortraitScreenshot/
    Automation/     UI Automation target discovery and access
    Capture/        Capture and scrolling workflow, options, and results
    Configuration/  Per-user application settings
    Imaging/        Screen capture, stitching, and image cropping
    Interop/         Native Windows APIs
    UI/              Windows Forms user interface
tests/
  LongPortraitScreenshot.SelfTest/  Dependency-free regression tests
```
