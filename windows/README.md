# BetterVoice for Windows

Native Windows port of **BetterVoice**, bringing offline speech recognition, developer vocabulary cleanup, circular pointer gestures with screenshot context, and seamless active-field text insertion to Windows 10 and 11.

The first launch downloads the selected Whisper model and warms its native runtime in the background, so the first completed recording does not pay model-initialization cost. **Balanced (base)** is the default; **Fast (tiny)** trades accuracy for speed, while **Accurate (small)** uses more memory and compute. English uses the corresponding English-only model and other languages use its multilingual variant. Whisper.net automatically prefers an available accelerated runtime (Vulkan on supported Windows GPUs) and falls back to CPU. Grammar correction is optional and preloads its pinned ONNX model and tokenizer when enabled.

For screen-recorded product demos, launch with `BETTERVOICE_CAPTURE_HUD=1` to
include the recording HUD. Normal launches exclude it from capture so it cannot
appear in saved context screenshots.

## Settings experience

The Windows settings window uses the native .NET 9 Fluent theme and a responsive
five-section control deck: Overview, Dictation, Visual context, Shortcuts, and
Storage. The Visual context section provides two mutually exclusive screenshot
modes: **Full display + highlight** (the default) preserves the surrounding
screen and marks the circled target, while **Cropped selection** captures and
pastes only the selected region. Changes save automatically with visible
feedback. Use `Ctrl+1` through `Ctrl+5` to move between sections, or `Esc` to
close the window.

## Architecture & Subsystems

| Subsystem | Windows Implementation | Role |
| :--- | :--- | :--- |
| **Math & Gesture Core** | `BetterVoice.Core` | 1:1 port of `CircleGestureDetector`, `TrailSegments`, `DeveloperTextCleanup`, `VocabularyFile`, `RecordingShortcutState`, `TranscriptionLanguage`. |
| **Pointer Trail & Overlay** | `TrailOverlayWindow` | Virtual screen click-through layered window (`WS_EX_LAYERED \| WS_EX_TRANSPARENT \| WS_EX_NOACTIVATE`) with `SetWindowDisplayAffinity(..., WDA_EXCLUDEFROMCAPTURE)`, non-blocking render dispatch, cached fade pens, and mode-aware full-display or crop confirmation. |
| **Input & Shortcuts** | `InputMonitor` | Decoupled `WH_KEYBOARD_LL` hook with lock-free `Channel<KeyEvent>` (preventing `LowLevelHooksTimeout` unhooking) and 60Hz mouse sampling. |
| **Audio Capture** | `AudioRecorder` | WASAPI 16kHz 16-bit mono recording with real-time RMS/peak level metering for live HUD waveforms. |
| **Context Capture** | `ScreenshotCapture` | Off-UI-thread, multi-monitor-aware PNG capture. Full-display mode saves the complete target monitor with a halo and circle around the referenced area; cropped-selection mode saves only the circled region with the same marker and a subtle boundary. |
| **Text Insertion** | `TextInsertion` | Focused-control restoration plus bounded Unicode injection (`KEYEVENTF_UNICODE`) for transcripts, with clipboard backup + `Ctrl+V` only when Windows rejects input injection. The image produced by the selected capture mode is then placed on the clipboard and pasted into the same captured target window. |
| **Grammar Correction** | `GrammarCorrector` | Preloaded ONNX Runtime (`Microsoft.ML.OnnxRuntime`) session running HuggingFace `t5-tiny-gec-hone` with automatic CPU thread selection. |
| **UI & Shell** | `TrayIconManager` & `SetupWindow` | Modern system tray notification icon, menus, and Fluent dark settings window. |

## Building & Testing

### Prerequisites
- .NET 9 SDK (or later)

### Run Unit Tests
```powershell
dotnet test windows/BetterVoice.sln
```
*Executes the core, integration, hardware, and performance test suites.*

### Build Release Application
```powershell
dotnet build windows/BetterVoice.sln -c Release
```
Output binary is generated at:
`windows/src/BetterVoice.App/bin/Release/net9.0-windows/BetterVoice.App.exe`
