using System;
using System.Runtime.InteropServices;

namespace PathHide.Views;

/// <summary>
/// The few Objective-C runtime calls the macOS menu bar needs, made directly. objc_msgSend takes the
/// signature of the method it calls, so each call shape has its own import. macOS only.
/// </summary>
internal static class ObjC
{
    private const string Runtime = "/usr/lib/libobjc.A.dylib";

    /// <summary>The type encoding of a method taking one object and returning BOOL, which is a C bool on
    /// Apple silicon and a signed char on Intel.</summary>
    internal static readonly string BoolMethodTypes =
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "B@:@" : "c@:@";

    /// <summary>The type encoding of an action method: no result, one sender.</summary>
    internal const string ActionMethodTypes = "v@:@";

    internal static IntPtr Class(string name) => objc_getClass(name);

    internal static IntPtr Sel(string name) => sel_registerName(name);

    internal static string SelectorName(IntPtr selector) =>
        selector == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(sel_getName(selector)) ?? "";

    internal static IntPtr Send(IntPtr receiver, string selector) => MsgSend(receiver, Sel(selector));

    internal static IntPtr Send(IntPtr receiver, string selector, IntPtr argument) =>
        MsgSend(receiver, Sel(selector), argument);

    internal static IntPtr Send(IntPtr receiver, string selector, IntPtr first, IntPtr second, IntPtr third) =>
        MsgSend(receiver, Sel(selector), first, second, third);

    internal static ulong SendForUInt(IntPtr receiver, string selector) => MsgSendForUInt(receiver, Sel(selector));

    internal static void SendUInt(IntPtr receiver, string selector, ulong argument) =>
        MsgSendUInt(receiver, Sel(selector), argument);

    internal static bool SendForBool(IntPtr receiver, string selector, IntPtr argument) =>
        MsgSendForBool(receiver, Sel(selector), argument) != 0;

    /// <summary>A new NSString, owned by the caller; the menu bar keeps its strings for the process's life.</summary>
    internal static IntPtr NSString(string text) =>
        MsgSendUtf8(Send(Class("NSString"), "alloc"), Sel("initWithUTF8String:"), text);

    internal static string String(IntPtr nsString) =>
        nsString == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(Send(nsString, "UTF8String")) ?? "";

    /// <summary>Defines a class; <paramref name="define"/> adds its methods before it is registered.</summary>
    internal static IntPtr DefineClass(string name, string superclass, Action<IntPtr> define)
    {
        var type = objc_allocateClassPair(Class(superclass), name, 0);
        if (type == IntPtr.Zero)
            throw new InvalidOperationException($"The Objective-C class {name} could not be defined.");
        define(type);
        objc_registerClassPair(type);
        return type;
    }

    /// <summary>
    /// Adds a method to <paramref name="type"/>; false when the class already defines one of that name,
    /// which is kept. The caller keeps <paramref name="implementation"/> alive for as long as the class
    /// can be sent the message.
    /// </summary>
    internal static bool AddMethod(IntPtr type, string selector, Delegate implementation, string types) =>
        class_addMethod(type, Sel(selector), Marshal.GetFunctionPointerForDelegate(implementation), types);

    [DllImport(Runtime)]
    private static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Runtime)]
    private static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Runtime)]
    private static extern IntPtr sel_getName(IntPtr selector);

    [DllImport(Runtime)]
    private static extern IntPtr objc_allocateClassPair(IntPtr superclass, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nint extraBytes);

    [DllImport(Runtime)]
    private static extern void objc_registerClassPair(IntPtr type);

    [DllImport(Runtime)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool class_addMethod(IntPtr type, IntPtr selector, IntPtr implementation, [MarshalAs(UnmanagedType.LPUTF8Str)] string types);

    [DllImport(Runtime, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector);

    [DllImport(Runtime, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector, IntPtr argument);

    [DllImport(Runtime, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector, IntPtr first, IntPtr second, IntPtr third);

    [DllImport(Runtime, EntryPoint = "objc_msgSend")]
    private static extern ulong MsgSendForUInt(IntPtr receiver, IntPtr selector);

    [DllImport(Runtime, EntryPoint = "objc_msgSend")]
    private static extern void MsgSendUInt(IntPtr receiver, IntPtr selector, ulong argument);

    [DllImport(Runtime, EntryPoint = "objc_msgSend")]
    private static extern byte MsgSendForBool(IntPtr receiver, IntPtr selector, IntPtr argument);

    [DllImport(Runtime, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSendUtf8(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string argument);
}
