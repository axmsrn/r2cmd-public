using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace R2Cmd;

// =============================================================================
// Closes a non-modal popup window as soon as the user clicks anywhere outside it.
//
// Only works for windows shown with Show(). A modal dialog disables its owner,
// so the click never reaches the owner, the popup keeps the activation, and
// Deactivated never fires — modally the window would close only when the whole
// application lost focus.
//
// Two details separate this from a bare "Deactivated -> Close()":
//
// 1. The close is deferred to the dispatcher. Destroying an owned window while
//    the activation change is still in flight leaves Windows without a valid
//    target, and it hands the foreground to the previously active application:
//    the file manager sinks behind whatever was in front of it, even though the
//    user clicked on the file manager itself.
//
// 2. The owner is re-activated afterwards, but only when the owner is the window
//    that actually received the click. If the user switched to another program,
//    pulling the file manager back to the front would be stealing their focus.
// =============================================================================
public static class CloseOnFocusLoss
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    public static void Enable(Window window)
    {
        // Guards against a second Deactivated arriving while the deferred close
        // is still queued, which would schedule the close twice
        bool closing = false;

        window.Closing += (s, e) => closing = true;

        window.Deactivated += (s, e) =>
        {
            if (closing) return;
            closing = true;

            var owner = window.Owner;

            window.Dispatcher.BeginInvoke(new Action(() =>
            {
                // Read the foreground window now, once the switch has settled
                IntPtr foreground = GetForegroundWindow();
                IntPtr ownerHandle = owner == null
                    ? IntPtr.Zero
                    : new WindowInteropHelper(owner).Handle;

                bool ownerWasClicked = ownerHandle != IntPtr.Zero && foreground == ownerHandle;

                window.Close();

                if (ownerWasClicked && owner != null && owner.IsVisible)
                {
                    owner.Activate();
                }
            }), DispatcherPriority.Background);
        };
    }
}
