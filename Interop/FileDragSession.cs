using System;
using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BetterQuickSettings.Interop;

/// <summary>One guarded OLE gesture with immediate visual cleanup on release, cancellation, or completion.</summary>
internal sealed class FileDragSession : IDisposable
{
    private readonly DispatcherTimer visualTimer;
    private readonly string path, origin;
    private readonly bool holdFile;
    private readonly BitmapSource? image;
    private readonly double dpi;
    private DragThumbnail? thumbnail;
    private bool cancelled, pointerFinished, disposed;
    private int queries;
    private DragDropKeyStates firstKeys;
    private bool firstPhysical;
    private DragDropEffects result;
    public bool PointerActive => !pointerFinished;
    public bool Completed { get; private set; }
    internal string Diagnostic => $"completed={Completed}; pointerFinished={pointerFinished}; cancelled={cancelled}; queries={queries}; firstKeys={firstKeys}; firstPhysical={firstPhysical}; result={result}";
    public event Action? PointerFinished;

    public FileDragSession(Dispatcher dispatcher, string path, string origin, bool holdFile, BitmapSource? image, double dpi)
    {
        this.path = path; this.origin = origin; this.holdFile = holdFile; this.image = image; this.dpi = dpi;
        visualTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input, (_, _) => TickVisual(), dispatcher);
        visualTimer.Stop();
    }

    public DragDropEffects Run(FrameworkElement source)
    {
        try
        {
            if (image is not null)
            {
                try { thumbnail = new DragThumbnail(IntPtr.Zero, image, dpi); }
                catch (Exception) { thumbnail?.Dispose(); thumbnail = null; }
            }
            var data = new DataObject();
            data.SetFileDropList(new StringCollection { path });
            data.SetData("Ada.FileShelf.Origin", origin);
            using var lease = holdFile ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
            source.AddHandler(DragDrop.QueryContinueDragEvent, new QueryContinueDragEventHandler(QueryContinue), true);
            visualTimer.Start();
            result = DragDrop.DoDragDrop(source, data, DragDropEffects.Copy);
            return result;
        }
        finally
        {
            source.RemoveHandler(DragDrop.QueryContinueDragEvent, new QueryContinueDragEventHandler(QueryContinue));
            Completed = true;
            EndPointer();
        }
    }

    private void QueryContinue(object sender, QueryContinueDragEventArgs args)
    {
        if (queries++ == 0) { firstKeys = args.KeyStates; firstPhysical = Native.Pressed(1); }
        if (cancelled || args.EscapePressed)
        { cancelled = true; EndPointer(); args.Action = DragAction.Cancel; }
        else if ((args.KeyStates & DragDropKeyStates.LeftMouseButton) == 0 || !Native.Pressed(1))
        {
            // Hide before invoking the receiving application's Drop handler.
            EndPointer();
            args.Action = DragAction.Drop;
        }
        else args.Action = DragAction.Continue;
        args.Handled = true;
    }

    private void TickVisual()
    {
        if (Native.Pressed(0x1B)) { Cancel(); return; }
        if (!Native.Pressed(1)) { EndPointer(); return; }
        thumbnail?.FollowPointer();
    }

    private void EndPointer()
    {
        if (pointerFinished) return;
        pointerFinished = true;
        visualTimer.Stop();
        thumbnail?.Dispose(); thumbnail = null;
        PointerFinished?.Invoke();
    }

    public void Cancel()
    {
        cancelled = true;
        EndPointer();
        if (!Completed)
        {
            // Wake OLE's modal input loop without synthesizing input into another application.
            Native.PostThreadMessage(Native.GetCurrentThreadId(), 0x100, (IntPtr)0x1B, IntPtr.Zero);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (!Completed) Cancel();
        EndPointer();
    }
}
