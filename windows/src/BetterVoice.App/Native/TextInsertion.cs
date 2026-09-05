using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using BetterVoice.Core;

namespace BetterVoice.App.Native;

public static class TextInsertion
{
    public readonly record struct AppContext(
        IntPtr HWnd,
        string ProcessName,
        DeveloperAppProfile Profile,
        IntPtr FocusedHWnd = default);

    public static AppContext GetCurrentContext()
    {
        IntPtr hwnd = Win32Api.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return new AppContext(IntPtr.Zero, "general", DeveloperAppProfile.General);
        }

        uint targetThreadId = Win32Api.GetWindowThreadProcessId(hwnd, out uint pid);
        IntPtr focusedHwnd = IntPtr.Zero;
        var threadInfo = new Win32Api.GUITHREADINFO
        {
            cbSize = Marshal.SizeOf<Win32Api.GUITHREADINFO>()
        };
        if (targetThreadId != 0 && Win32Api.GetGUIThreadInfo(targetThreadId, ref threadInfo))
        {
            focusedHwnd = threadInfo.hwndFocus;
        }
        string processName = "unknown";
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            processName = proc.ProcessName;
        }
        catch
        {
            // ignored
        }

        var profile = DeveloperAppProfileExtensions.Infer(processName, processName);
        return new AppContext(hwnd, processName, profile, focusedHwnd);
    }

    public static async Task InsertTextAsync(string text, AppContext context)
    {
        if (string.IsNullOrEmpty(text)) return;

        await FocusTargetAsync(context);

        // Unicode input avoids a race between the transcript clipboard and the
        // image clipboard. Bounded batches keep long transcripts from overrunning
        // slower editors while preserving newlines and the user's clipboard.
        if (await SendUnicodeStringChunkedAsync(text))
        {
            return;
        }

        // Fall back to clipboard paste only if Windows rejects the Unicode input
        // before it can be queued.
        string? previousText = null;
        bool hadText = false;

        try
        {
            if (System.Windows.Clipboard.ContainsText())
            {
                previousText = System.Windows.Clipboard.GetText();
                hadText = true;
            }
        }
        catch
        {
            // Clipboard access can intermittently fail if locked by another process
        }

        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch
        {
            // Fallback to direct unicode if clipboard is locked
            await SendUnicodeStringChunkedAsync(text);
            return;
        }

        uint insertionClipboardSequence = Win32Api.GetClipboardSequenceNumber();

        // Send Ctrl+V
        SendCtrlV();

        // SendInput queues keyboard messages; keep the text clipboard stable long
        // enough for rich editors to consume the first paste before a context
        // image replaces it.
        await Task.Delay(140);

        if (hadText && previousText != null)
        {
            // Do not keep the delivery path waiting for clipboard restoration.
            // The sequence guard avoids overwriting a newer clipboard value that
            // the user copied while the target application consumed the paste.
            _ = RestoreClipboardAfterPasteAsync(previousText, insertionClipboardSequence);
        }
    }

    public static async Task<int> InsertImagesAsync(
        IReadOnlyList<string> imagePaths,
        AppContext context,
        bool waitForPriorPaste)
    {
        string[] existingImages = imagePaths
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .ToArray();
        if (existingImages.Length == 0) return 0;

        await FocusTargetAsync(context);
        if (waitForPriorPaste)
        {
            // Give the destination time to consume the transcript before the
            // clipboard is replaced with the first cropped context image.
            await Task.Delay(320);
        }

        int pastedCount = 0;
        for (int index = 0; index < existingImages.Length; index++)
        {
            BitmapImage image;
            try
            {
                image = LoadClipboardImage(existingImages[index]);
            }
            catch
            {
                continue;
            }

            bool copied = false;
            for (int attempt = 0; attempt < 3 && !copied; attempt++)
            {
                try
                {
                    System.Windows.Clipboard.SetImage(image);
                    copied = true;
                }
                catch
                {
                    if (attempt < 2)
                    {
                        await Task.Delay(40 * (attempt + 1));
                    }
                }
            }

            if (!copied) continue;

            SendCtrlV();
            pastedCount++;

            if (index < existingImages.Length - 1)
            {
                // Rich editors consume bitmap clipboard data asynchronously.
                await Task.Delay(350);
            }
        }

        return pastedCount;
    }

    private static async Task FocusTargetAsync(AppContext context)
    {
        // Transcription can take long enough for another window to briefly receive
        // focus. Reattach to the captured UI thread so the same focused control,
        // not merely the same top-level window, receives the paste gesture.
        if (context.HWnd == IntPtr.Zero || !Win32Api.IsWindow(context.HWnd)) return;

        uint targetThreadId = Win32Api.GetWindowThreadProcessId(context.HWnd, out _);
        uint currentThreadId = Win32Api.GetCurrentThreadId();
        bool attached = targetThreadId != 0 &&
                        targetThreadId != currentThreadId &&
                        Win32Api.AttachThreadInput(currentThreadId, targetThreadId, true);
        try
        {
            Win32Api.SetForegroundWindow(context.HWnd);
            if (context.FocusedHWnd != IntPtr.Zero && Win32Api.IsWindow(context.FocusedHWnd))
            {
                Win32Api.SetFocus(context.FocusedHWnd);
            }
        }
        finally
        {
            if (attached)
            {
                Win32Api.AttachThreadInput(currentThreadId, targetThreadId, false);
            }
        }

        // SetForegroundWindow and input-queue attachment are asynchronous from
        // the destination application's perspective.
        await Task.Delay(120);
    }

    private static BitmapImage LoadClipboardImage(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(Path.GetFullPath(path), UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static async Task RestoreClipboardAfterPasteAsync(string previousText, uint insertionSequence)
    {
        await Task.Delay(300);
        if (Win32Api.GetClipboardSequenceNumber() != insertionSequence) return;

        try
        {
            System.Windows.Clipboard.SetText(previousText);
        }
        catch
        {
            // Clipboard restoration is best-effort.
        }
    }

    private static async Task<bool> SendUnicodeStringChunkedAsync(string text)
    {
        const int chunkSize = 24;
        for (int offset = 0; offset < text.Length; offset += chunkSize)
        {
            int length = Math.Min(chunkSize, text.Length - offset);
            string chunk = text.Substring(offset, length);
            if (!SendUnicodeString(chunk)) return false;
            if (offset + length < text.Length)
            {
                await Task.Delay(8);
            }
        }

        return true;
    }

    private static bool SendUnicodeString(string text)
    {
        var inputs = new Win32Api.INPUT[text.Length * 2];
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            inputs[i * 2] = new Win32Api.INPUT
            {
                type = Win32Api.INPUT_KEYBOARD,
                u = new Win32Api.InputUnion
                {
                    ki = new Win32Api.KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = c,
                        dwFlags = Win32Api.KEYEVENTF_UNICODE
                    }
                }
            };

            inputs[i * 2 + 1] = new Win32Api.INPUT
            {
                type = Win32Api.INPUT_KEYBOARD,
                u = new Win32Api.InputUnion
                {
                    ki = new Win32Api.KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = c,
                        dwFlags = Win32Api.KEYEVENTF_UNICODE | Win32Api.KEYEVENTF_KEYUP
                    }
                }
            };
        }

        uint sent = Win32Api.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Win32Api.INPUT>());
        return sent == inputs.Length;
    }

    private static void SendCtrlV()
    {
        const ushort VK_CONTROL = 0x11;
        const ushort VK_V = 0x56;

        var inputs = new Win32Api.INPUT[4];

        // Ctrl down
        inputs[0] = new Win32Api.INPUT
        {
            type = Win32Api.INPUT_KEYBOARD,
            u = new Win32Api.InputUnion { ki = new Win32Api.KEYBDINPUT { wVk = VK_CONTROL, dwFlags = 0 } }
        };

        // V down
        inputs[1] = new Win32Api.INPUT
        {
            type = Win32Api.INPUT_KEYBOARD,
            u = new Win32Api.InputUnion { ki = new Win32Api.KEYBDINPUT { wVk = VK_V, dwFlags = 0 } }
        };

        // V up
        inputs[2] = new Win32Api.INPUT
        {
            type = Win32Api.INPUT_KEYBOARD,
            u = new Win32Api.InputUnion { ki = new Win32Api.KEYBDINPUT { wVk = VK_V, dwFlags = Win32Api.KEYEVENTF_KEYUP } }
        };

        // Ctrl up
        inputs[3] = new Win32Api.INPUT
        {
            type = Win32Api.INPUT_KEYBOARD,
            u = new Win32Api.InputUnion { ki = new Win32Api.KEYBDINPUT { wVk = VK_CONTROL, dwFlags = Win32Api.KEYEVENTF_KEYUP } }
        };

        Win32Api.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Win32Api.INPUT>());
    }
}
