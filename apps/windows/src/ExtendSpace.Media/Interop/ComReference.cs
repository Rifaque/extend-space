using System.Runtime.InteropServices;

namespace ExtendSpace.Media.Interop;

internal static class ComReference
{
    /// <summary>
    /// Drops one reference to a COM object now instead of whenever the garbage collector runs.
    /// GPU textures and encoder samples are pooled, so holding them until a collection would stall
    /// the pipeline. Release each object obtained from an API once; interface casts of the same
    /// object share its reference and must not be released separately.
    /// </summary>
    public static void Release<T>(ref T? comObject)
        where T : class
    {
        T? local = comObject;
        comObject = null;
        if (local is not null && Marshal.IsComObject(local))
        {
            Marshal.ReleaseComObject(local);
        }
    }

    public static void Release(object? comObject) => Release(ref comObject);
}
