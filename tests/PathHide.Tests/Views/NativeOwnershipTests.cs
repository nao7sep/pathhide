using System;
using System.Runtime.InteropServices;
using PathHide.Views;
using Xunit;
using static PathHide.Views.MacMenuBar;

namespace PathHide.Tests.Views;

public sealed class NativeOwnershipTests
{
    [MacOnlyFact]
    public void Rebuilt_bars_and_their_children_are_released_after_the_owner_finishes()
    {
        LoadAppKit();
        for (var build = 0; build < 3; build++)
        {
            var bar = BuildBar("PathHide", IntPtr.Zero);
            using var root = new WeakObject(bar.Bar);
            using var services = new WeakObject(bar.Services);
            using var window = new WeakObject(bar.Window);
            var edit = ObjC.Send(ObjC.SendWithIndex(bar.Bar, "itemAtIndex:", 1), "submenu");
            using var item = new WeakObject(ObjC.SendWithIndex(edit, "itemAtIndex:", 0));
            Assert.True(root.IsAlive && services.IsAlive && window.IsAlive && item.IsAlive);

            bar.Dispose();
            bar.Dispose();

            Assert.False(root.IsAlive);
            Assert.False(services.IsAlive);
            Assert.False(window.IsAlive);
            Assert.False(item.IsAlive);
        }
    }

    [MacOnlyFact]
    public void Construction_scopes_release_an_attached_item_and_its_parent_when_building_throws()
    {
        LoadAppKit();
        WeakObject? parent = null;
        WeakObject? child = null;
        try
        {
            Assert.Throws<InvalidOperationException>((Action)(() =>
            {
                using var menu = NewMenu("Unfinished menu");
                parent = new WeakObject(menu.Pointer);
                using var item = NewItem(new Item("Unfinished item", "copy:"), IntPtr.Zero);
                child = new WeakObject(item.Pointer);
                ObjC.Send(menu.Pointer, "addItem:", item.Pointer);
                throw new InvalidOperationException("Construction stopped after attaching a child.");
            }));
            Assert.NotNull(parent);
            Assert.NotNull(child);
            Assert.False(parent.IsAlive);
            Assert.False(child.IsAlive);
        }
        finally
        {
            parent?.Dispose();
            child?.Dispose();
        }
    }

    [MacOnlyFact]
    public void Owned_strings_are_released_and_disposal_is_idempotent()
    {
        LoadAppKit();
        var text = new ObjC.OwnedObject(ObjC.NSString(new string('x', 256) + Guid.NewGuid()));
        using var weak = new WeakObject(text.Pointer);
        Assert.True(weak.IsAlive);
        text.Dispose();
        text.Dispose();
        Assert.Equal(IntPtr.Zero, text.Pointer);
        Assert.False(weak.IsAlive);
    }

    private static void LoadAppKit() =>
        NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");

    // A zeroing weak reference observes actual native deallocation without sending to a dead object
    // or depending on retainCount, which AppKit is free to change internally.
    private sealed class WeakObject : IDisposable
    {
        private readonly IntPtr _slot = Marshal.AllocHGlobal(IntPtr.Size);

        public WeakObject(IntPtr value)
        {
            Assert.NotEqual(IntPtr.Zero, value);
            Marshal.WriteIntPtr(_slot, IntPtr.Zero);
            objc_initWeak(_slot, value);
        }

        public bool IsAlive
        {
            get
            {
                var value = objc_loadWeakRetained(_slot);
                if (value == IntPtr.Zero)
                    return false;
                ObjC.Send(value, "release");
                return true;
            }
        }

        public void Dispose()
        {
            objc_destroyWeak(_slot);
            Marshal.FreeHGlobal(_slot);
        }
    }

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr objc_initWeak(IntPtr location, IntPtr value);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr objc_loadWeakRetained(IntPtr location);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern void objc_destroyWeak(IntPtr location);
}
