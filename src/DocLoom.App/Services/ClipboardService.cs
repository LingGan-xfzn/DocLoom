using System.Runtime.InteropServices;
using System.Windows;

namespace DocLoom.App.Services;

/// <summary>剪贴板写入。WPF 的剪贴板会被其他进程短暂占用，这里做几次退避重试。</summary>
internal static class ClipboardService
{
    public static bool TrySetText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, true);
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(40 + attempt * 40);
            }
            catch (ExternalException)
            {
                Thread.Sleep(40 + attempt * 40);
            }
        }

        return false;
    }
}