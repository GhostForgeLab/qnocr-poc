using System;
using System.IO;
using System.Threading;
using QnocrPoc.Shadow;
using Forms = System.Windows.Forms;

namespace QNAgent;

internal static class ClipboardChecks
{
    public static int Run()
    {
        // Materialize every original format before changing anything; never log clipboard contents.
        var original = Forms.Clipboard.GetDataObject();
        var snapshot = new Forms.DataObject();
        if (original is not null)
            foreach (var format in original.GetFormats(autoConvert: false))
            {
                var value = original.GetData(format, autoConvert: false) ?? throw new IOException("Cannot preserve clipboard format: " + format);
                if (value is System.Drawing.Image image) value = image.Clone();
                if (value is MemoryStream stream) value = new MemoryStream(stream.ToArray());
                snapshot.SetData(format, autoConvert: false, value);
            }
        var expected = "剪贴板回归检查：粗支 / 细支，请先核实。\r\n第二行 298.00 元";
        try
        {
            var clipboard = new WindowsAssistantClipboard();
            clipboard.SetText(expected);
            string? actual = null; Exception? error = null;
            var reader = new Thread(() => { try { actual = Forms.Clipboard.GetText(); } catch (Exception ex) { error = ex; } });
            reader.SetApartmentState(ApartmentState.STA); reader.Start(); reader.Join();
            if (error is not null) throw error;
            if (actual != expected) throw new Exception("Clipboard text did not survive the writer STA thread.");
            clipboard.SetText(expected + "（再次复制）");
            if (Forms.Clipboard.GetText() != expected + "（再次复制）") throw new Exception("Repeat clipboard write did not replace the prior text.");
            Console.WriteLine("PASS: exact Unicode reply persists after clipboard writer exits");
            Console.WriteLine("PASS: repeated copy replaces clipboard content");
            return 0;
        }
        finally
        {
            if (original is null) Forms.Clipboard.Clear();
            else Forms.Clipboard.SetDataObject(snapshot, true, 5, 80);
            Console.WriteLine("Original clipboard formats restored.");
        }
    }
}
